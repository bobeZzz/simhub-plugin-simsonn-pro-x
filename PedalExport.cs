using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimsonnPedals
{
    /// <summary>
    /// Direct mode (phase D2): turns a preset into the pedals' own settings, so the pedals output what vJoy mode sends
    /// with that preset. The inverse of <see cref="ManagerImport"/>. Only the shaping of throttle, brake (and clutch) is
    /// replaced: output ends (dead zones, calibration and brake force folded in), curve points and invert; filter off.
    /// Calibration fields, sensors, lighting and everything else stay byte for byte.
    ///
    /// The reference is the pedals set up neutral (<see cref="PedalSettingsLayout.BaseSetup"/>): each axis outputs
    /// 0…100 % over its raw range (+6…+8), which is what the plugin's calibration and force scaling were set against.
    /// Exporting from any block gives the same result, so exports never stack.
    /// </summary>
    public static class PedalExport
    {
        public sealed class Result
        {
            public bool Success { get; set; }
            public byte[] Block { get; set; }

            /// <summary>True if <see cref="Block"/> differs from the block it was made from (a write is needed).</summary>
            public bool Changed { get; set; }

            public List<string> Lines { get; } = new List<string>();
            public override string ToString() => string.Join(Environment.NewLine, Lines);
        }

        private const double DirectInputMax = PedalConfig.RawMax;

        // Per axis: the Manager's dead zone percentages (+34/+35), shown in the Manager only; the firmware ignores them.
        private static readonly HashSet<int> DisplayOnly = new HashSet<int>(
            Enumerable.Range(0, 8).SelectMany(axis => new[] { PedalSettingsLayout.AxisOffset(axis) + 34, PedalSettingsLayout.AxisOffset(axis) + 35 }));

        /// <summary>Offsets where two blocks differ in anything the pedals act on (display-only bytes left out).</summary>
        public static List<int> Differences(byte[] a, byte[] b) =>
            Enumerable.Range(0, Math.Min(a.Length, b.Length)).Where(i => a[i] != b[i] && !DisplayOnly.Contains(i)).ToList();

        /// <summary>
        /// True if <paramref name="to"/> differs from <paramref name="from"/> only in the brake's centre and output high
        /// end, which the live <c>damping_max</c> command sets (a 200 ms pause instead of a 2 s write).
        /// </summary>
        public static bool OnlyBrakeHighEnd(byte[] from, byte[] to)
        {
            int brake = PedalSettingsLayout.AxisOffset(PedalSettingsLayout.BrakeAxis(from));
            var diff = Differences(from, to);
            return diff.Count > 0 && diff.All(i => i >= brake + 2 && i < brake + 6);
        }

        public static Result ToBlock(byte[] block, PedalPreset preset, bool clutch)
        {
            var result = new Result();
            if (!PedalSettingsLayout.IsLayoutVerified(block))
            {
                result.Lines.Add($"Not exported: the plugin doesn't know this settings layout (firmware {PedalSettingsLayout.FirmwareVersionText(block)}).");
                return result;
            }

            var output = Axis(block, PedalSettingsLayout.ThrottleAxis(block), preset.Throttle, 1, "Throttle", result);

            var brake = preset.Brake;
            double force = brake.HasForceScaling ? brake.FirmwareMaxForceKg / Math.Min(brake.MaxForceKg, brake.FirmwareMaxForceKg) : 1;
            output = Axis(output, PedalSettingsLayout.BrakeAxis(block), brake, force, "Brake", result);
            double? pedalsKg = PedalSettingsLayout.BrakeMaxForceKg(block);
            if (brake.HasForceScaling && pedalsKg != null && Math.Abs(pedalsKg.Value - brake.FirmwareMaxForceKg) > 0.5)
                result.Lines.Add($"  The pedals are set up for {pedalsKg:0.#} kg but the preset's Pedal max says {brake.FirmwareMaxForceKg:0.#} kg: " +
                                 "the feel matches vJoy mode, but \"100% at\" isn't in real kg until Pedal max matches.");

            int clutchAxis = PedalSettingsLayout.ClutchAxis(block);
            if (clutch && PedalSettingsLayout.IsAxisUsed(block, clutchAxis))
                output = Axis(output, clutchAxis, preset.Clutch, 1, "Clutch", result);
            else if (clutch)
                result.Lines.Add("Clutch: not in use in the pedals' settings, left as it is.");

            result.Block = output;
            result.Changed = Differences(block, output).Count > 0;
            result.Success = true;
            return result;
        }

        /// <summary>
        /// The plugin maps DirectInput r (0…65535 over the raw range) to x = (r − calibration min) / calibration span,
        /// optionally inverts it, multiplies by <paramref name="force"/> (brake "100% at"), applies the dead zone and the
        /// curve. All but the curve is one clamped straight line, so it becomes the firmware's output ends: the raw values
        /// where the output leaves 0 and reaches 100 %. Invert in the firmware comes after the curve, so an inverted axis
        /// gets the mirrored curve and the firmware's invert bit, which together give the plugin's result.
        /// </summary>
        private static byte[] Axis(byte[] block, int axis, PedalConfig config, double force, string name, Result result)
        {
            var a = PedalSettingsLayout.ReadAxis(block, axis);
            double span = a.RawMax - a.RawMin;
            int calLow = Math.Min(config.CalibrationMin, config.CalibrationMax), calHigh = Math.Max(config.CalibrationMin, config.CalibrationMax);
            if (span <= 0 || calHigh <= calLow)
            {
                result.Lines.Add($"{name}: no usable range (raw {a.RawMin}…{a.RawMax}, calibration {calLow}…{calHigh}), left as it is.");
                return block;
            }

            // Plugin position x (before force scaling) where the output starts and reaches 100 %.
            double start = config.DeadzoneLow / 100 / force, end = config.DeadzoneHigh / 100 / force;
            if (config.Invert) (start, end) = (1 - end, 1 - start);
            int RawAt(double x) => (int)Math.Round(a.RawMin + (calLow + x * (calHigh - calLow)) / DirectInputMax * span);
            int outLow = RawAt(start), outHigh = Math.Max(RawAt(end), RawAt(start) + 1);
            outLow = Math.Max(short.MinValue, Math.Min(short.MaxValue - 1, outLow));
            outHigh = Math.Max(outLow + 1, Math.Min(short.MaxValue, outHigh));

            var p = config.CurvePoints;
            var curve = Enumerable.Range(0, PedalConfig.CurvePointCount)
                .Select(i => config.Invert ? 100 - 2 * p[PedalConfig.CurvePointCount - 1 - i] : 2 * p[i] - 100)
                .Select(v => (sbyte)Math.Max(-100, Math.Min(100, Math.Round(v))))
                .ToArray();

            result.Lines.Add($"{name}: 0 % at raw {outLow}, 100 % at raw {outHigh} " +
                             $"({(outLow - a.RawMin) / span * 100:0.#}–{(outHigh - a.RawMin) / span * 100:0.#} % of the range), " +
                             (curve.Select((v, i) => v == -100 + 20 * i).All(x => x) ? "linear" : "curve " + string.Join(" ", curve.Select(v => ((v + 100) / 2.0).ToString("0.#", CultureInfo.InvariantCulture)))) +
                             (config.Invert ? ", inverted" : ""));
            return PedalSettingsLayout.WithAxisShaping(block, axis, outLow, outHigh, config.Invert, curve);
        }
    }
}
