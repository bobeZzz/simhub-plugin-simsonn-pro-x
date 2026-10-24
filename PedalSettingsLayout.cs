using System;
using System.Linq;

namespace SimsonnPedals
{
    /// <summary>
    /// Fields of the pedals' settings block that the plugin understands, as mapped from Simsonn Manager
    /// one-change tests (research/pedal-settings-layout.md). Everything else is passed through untouched.
    /// </summary>
    public static class PedalSettingsLayout
    {
        /// <summary>
        /// First of the three pedal axes (throttle, brake, clutch) in use: 0 on 2.0.x, 3 on 2.1.x. Simsonn Manager's 2.1
        /// defaults, which 2.1.1 reset the rig to, switch axes 0–2 off (source pin −1) and drive axes 3–5 from the same
        /// sensors. The params report's raw channel i belongs to axis i, so the same index picks the raw channel.
        /// </summary>
        public static int PedalAxisBase(byte[] block) => !IsAxisUsed(block, 0) && IsAxisUsed(block, 3) ? 3 : 0;

        public static int ThrottleAxis(byte[] block) => PedalAxisBase(block);
        public static int BrakeAxis(byte[] block) => PedalAxisBase(block) + 1;
        public static int ClutchAxis(byte[] block) => PedalAxisBase(block) + 2;

        private static bool IsAxisUsed(byte[] block, int axis) => block[AxisBase + AxisSize * axis + SourceMain] != SourceNone;

        /// <summary>
        /// Raw load-cell units per kg of max force on a calibrated brake, or null for a firmware the plugin doesn't know.
        /// Simsonn Manager reads the version word as hex digits ("2080" → 208): up to 209 it uses 300 (confirmed on the
        /// rig with 2.0.8), from 210 on 148 (from the Manager's code, not tested on the rig).
        /// </summary>
        public static double? UnitsPerKg(byte[] block)
        {
            int? version = FirmwareVersionCode(block);
            if (version >= 200 && version <= 209) return 300;
            if (version >= 210 && version <= 219) return 148;
            return null;
        }

        /// <summary>Firmware version as Simsonn Manager compares it (0x2080 → 208), or null if it isn't decimal digits.</summary>
        public static int? FirmwareVersionCode(byte[] block)
        {
            string hex = BitConverter.ToUInt16(block, 0).ToString("X4").Substring(0, 3);
            return hex.All(char.IsDigit) ? int.Parse(hex) : (int?)null;
        }

        /// <summary>
        /// True for firmware whose settings layout the plugin knows: 2.0.x (mapped on the rig) and 2.1.x (the 2.1.1 dump
        /// after the rig's update has the same axis entries, with the pedals on axes 3–5), and only when the throttle
        /// and brake axes are actually in use.
        /// </summary>
        public static bool IsLayoutVerified(byte[] block)
        {
            int? version = FirmwareVersionCode(block);
            return version >= 200 && version <= 219 && IsAxisUsed(block, ThrottleAxis(block)) && IsAxisUsed(block, BrakeAxis(block));
        }

        /// <summary>Firmware version for display, e.g. "2.0.8".</summary>
        public static string FirmwareVersionText(byte[] block)
        {
            int? code = FirmwareVersionCode(block);
            return code == null ? $"0x{BitConverter.ToUInt16(block, 0):X4}" : $"{code / 100}.{code / 10 % 10}.{code % 10}";
        }

        private const int AxisBase = 0x40;
        private const int AxisSize = 52;
        private const int OutLow = 0, OutCentre = 2, OutHigh = 4, RawMin = 6, RawMax = 8;
        private const int Flags = 10, Curve = 11, CurvePoints = 11, DeadzoneStart = 34, DeadzoneEndGap = 35;
        private const int FilterMask = 0xE0, FilterShift = 5;
        private const int SourceMain = 24; // ADC pin feeding the axis (11 on this rig), 0xFF = off
        private const byte SourceNone = 0xFF;

        public sealed class AxisSettings
        {
            public int RawMin { get; set; }
            public int RawMax { get; set; }
            public int DeadzoneStart { get; set; }
            public int DeadzoneEnd { get; set; }
            public int Filter { get; set; }
            public sbyte[] Curve { get; set; }

            public bool IsLinear => Curve.Select((p, i) => p == -100 + 20 * i).All(x => x);
        }

        public static AxisSettings ReadAxis(byte[] block, int axis)
        {
            int o = AxisBase + AxisSize * axis;
            return new AxisSettings
            {
                RawMin = BitConverter.ToInt16(block, o + RawMin),
                RawMax = BitConverter.ToInt16(block, o + RawMax),
                DeadzoneStart = block[o + DeadzoneStart],
                DeadzoneEnd = 100 - block[o + DeadzoneEndGap],
                Filter = (block[o + Flags] & FilterMask) >> FilterShift,
                Curve = Enumerable.Range(0, CurvePoints).Select(i => unchecked((sbyte)block[o + Curve + i])).ToArray(),
            };
        }

        /// <summary>
        /// Max force the brake is set to, in kg (span between rest and full press / units per kg), or null if the
        /// firmware's units per kg aren't known.
        /// </summary>
        public static double? BrakeMaxForceKg(byte[] block)
        {
            var brake = ReadAxis(block, BrakeAxis(block));
            double? units = UnitsPerKg(block);
            return units == null ? (double?)null : Math.Round((brake.RawMax - brake.RawMin) / units.Value, 1);
        }

        /// <summary>One line per pedal, in the terms Simsonn Manager uses.</summary>
        public static string Describe(byte[] block)
        {
            string Axis(string name, int axis, string extra)
            {
                var a = ReadAxis(block, axis);
                string curve = a.IsLinear ? "linear" : "custom curve";
                string filter = a.Filter == 0 ? "filter off" : $"filter {a.Filter}";
                return $"{name}: dead zone {a.DeadzoneStart}–{a.DeadzoneEnd}, {curve}, {filter}{extra}";
            }
            double? kg = BrakeMaxForceKg(block);
            string maxForce = kg == null
                ? $", max force unknown (firmware {FirmwareVersionText(block)})"
                : $", max force {kg:0.#} kg";
            return Axis("Throttle", ThrottleAxis(block), "") + Environment.NewLine + Axis("Brake", BrakeAxis(block), maxForce);
        }

        /// <summary>True if the block already matches what <see cref="BaseSetup"/> would produce.</summary>
        public static bool IsBaseSetup(byte[] block, double brakeMaxForceKg) =>
            BaseSetup(block, brakeMaxForceKg).SequenceEqual(block);

        /// <summary>
        /// Copy of <paramref name="block"/> with throttle and brake set up for shaping in the plugin: linear curve,
        /// no dead zone, filter off, and the brake's max force set to <paramref name="brakeMaxForceKg"/>.
        /// Calibration (each pedal's rest value) is kept. Throws <see cref="NotSupportedException"/> on a firmware whose
        /// units per kg aren't known, since the brake's max force can't be set there.
        /// </summary>
        public static byte[] BaseSetup(byte[] block, double brakeMaxForceKg)
        {
            if (!IsLayoutVerified(block))
                throw new NotSupportedException(
                    $"The plugin doesn't know this settings layout (firmware {FirmwareVersionText(block)}, or the throttle/brake axes are switched off). " +
                    "Use Read pedal settings and share the dump so the layout can be checked.");
            double unitsPerKg = UnitsPerKg(block) ?? throw new NotSupportedException(
                $"The plugin doesn't know how firmware {FirmwareVersionText(block)} scales max force.");
            var result = (byte[])block.Clone();
            foreach (int axis in new[] { ThrottleAxis(block), BrakeAxis(block) })
            {
                int o = AxisBase + AxisSize * axis;
                int rawMin = BitConverter.ToInt16(block, o + RawMin);
                int rawMax = BitConverter.ToInt16(block, o + RawMax);
                if (axis == BrakeAxis(block))
                {
                    long target = rawMin + (long)Math.Round(brakeMaxForceKg * unitsPerKg);
                    if (brakeMaxForceKg < 5 || target > short.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(brakeMaxForceKg),
                            $"Max force must be between 5 and {MaxBrakeForceKg(block):0} kg for this calibration.");
                    rawMax = (int)target;
                    WriteInt16(result, o + RawMax, rawMax);
                }

                // No dead zone: the output range is the whole calibrated range.
                WriteInt16(result, o + OutLow, rawMin);
                WriteInt16(result, o + OutHigh, rawMax);
                WriteInt16(result, o + OutCentre, (rawMin + rawMax) / 2);
                result[o + DeadzoneStart] = 0;
                result[o + DeadzoneEndGap] = 0;
                result[o + Flags] = (byte)(block[o + Flags] & ~FilterMask);
                for (int i = 0; i < CurvePoints; i++) result[o + Curve + i] = unchecked((byte)(sbyte)(-100 + 20 * i));
            }
            return result;
        }

        /// <summary>Smallest raw travel accepted as a calibration (Simsonn Manager's limit too).</summary>
        public const int MinCalibrationSpan = 1000;

        /// <summary>
        /// Copy of <paramref name="block"/> with new calibration, then set up like <see cref="BaseSetup"/>: throttle raw
        /// range = rest…full press, brake rest = <paramref name="brakeRest"/> and its full press from the max force.
        /// Simsonn Manager's calibration writes the same raw fields (+6/+8, its old_calib_min/max).
        /// </summary>
        public static byte[] Calibrate(byte[] block, int throttleRest, int throttleFull, int brakeRest, double brakeMaxForceKg)
        {
            if (throttleFull - throttleRest < MinCalibrationSpan)
                throw new ArgumentOutOfRangeException(nameof(throttleFull), "The throttle didn't move enough: press it fully during calibration.");
            var result = (byte[])block.Clone();
            int throttle = AxisBase + AxisSize * ThrottleAxis(block);
            WriteInt16(result, throttle + RawMin, throttleRest);
            WriteInt16(result, throttle + RawMax, throttleFull);
            WriteInt16(result, AxisBase + AxisSize * BrakeAxis(block) + RawMin, brakeRest);
            return BaseSetup(result, brakeMaxForceKg);
        }

        /// <summary>The calibration stored in the pedals, for display.</summary>
        public static string DescribeCalibration(byte[] block)
        {
            var throttle = ReadAxis(block, ThrottleAxis(block));
            var brake = ReadAxis(block, BrakeAxis(block));
            return $"Throttle raw {throttle.RawMin} (rest) … {throttle.RawMax} (full); brake raw {brake.RawMin} (rest)";
        }

        /// <summary>Highest max force the brake's calibration leaves room for (the firmware must be known).</summary>
        public static double MaxBrakeForceKg(byte[] block) =>
            Math.Floor((short.MaxValue - ReadAxis(block, BrakeAxis(block)).RawMin) / UnitsPerKg(block).Value);

        private static void WriteInt16(byte[] block, int offset, int value)
        {
            var bytes = BitConverter.GetBytes((short)value);
            block[offset] = bytes[0];
            block[offset + 1] = bytes[1];
        }
    }
}
