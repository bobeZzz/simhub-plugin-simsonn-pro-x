using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SimsonnPedals
{
    /// <summary>
    /// Brings Simsonn Manager's pedal shaping (dead zones, curve, brake max force) into a plugin preset so the output is the
    /// same as the pedals gave with those settings. From the firmware (2.1.1 main image, see research/curves.md): the
    /// pedals map raw → −32767…32767 linearly between the output ends (+0/+4, which hold the dead zones), then apply the 11
    /// curve points with straight segments, which is exactly the plugin's dead zone → curve. Source is either the pedals'
    /// settings block or a Manager preset file (INI with the same FreeJoy fields).
    /// </summary>
    public static class ManagerImport
    {
        public sealed class Result
        {
            public bool Success { get; set; }
            public List<string> Lines { get; } = new List<string>();
            public override string ToString() => string.Join(Environment.NewLine, Lines);
        }

        /// <summary>
        /// Builds just enough of a settings block from a Manager preset file (version word, per axis the output ends, raw
        /// range, flags, curve and source pin) for <see cref="PedalSettingsLayout"/> to read it.
        /// </summary>
        public static byte[] BlockFromIni(IEnumerable<string> lines)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> current = null;
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                    sections[line.Substring(1, line.Length - 2)] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                else if (current != null && line.Contains("="))
                    current[line.Substring(0, line.IndexOf('=')).Trim()] = line.Substring(line.IndexOf('=') + 1).Trim();
            }

            int Int(string section, string key, int fallback = 0) =>
                sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) &&
                int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

            if (!sections.TryGetValue("DeviceUsbConfig", out var usb) || !usb.TryGetValue("FirmwareVersion", out var version) ||
                !sections.ContainsKey("AxesConfig_0"))
                throw new FormatException("This isn't a Simsonn Manager pedal preset (no [DeviceUsbConfig] FirmwareVersion or [AxesConfig_0]).");

            var block = new byte[PedalSettingsLayout.BlockSize];
            // The Manager writes the version word as its hex digits ("2100" = 0x2100).
            WriteInt16(block, 0, Convert.ToUInt16(version, 16));
            for (int axis = 0; axis < 8; axis++)
            {
                string a = $"AxesConfig_{axis}", c = $"AxesCurvesConfig_{axis}";
                if (!sections.ContainsKey(a)) continue;
                int o = PedalSettingsLayout.AxisOffset(axis);
                WriteInt16(block, o + 0, Int(a, "CalibMin"));
                WriteInt16(block, o + 2, Int(a, "CalibCenter"));
                WriteInt16(block, o + 4, Int(a, "CalibMax"));
                WriteInt16(block, o + 6, Int(a, "old_calib_min", -32767));
                WriteInt16(block, o + 8, Int(a, "old_calib_max", 32767));
                block[o + 10] = (byte)((Int(a, "OutEnabled") & 1) | (Int(a, "Inverted") & 1) << 1 | (Int(a, "Filter") & 7) << 5);
                for (int i = 0; i < 11; i++) block[o + 11 + i] = unchecked((byte)(sbyte)Int(c, $"Point_{i}", -100 + 20 * i));
                block[o + 24] = unchecked((byte)(sbyte)Int(a, "SourceMain", -1));
                block[o + 34] = (byte)Int(a, "left_deadband_size");
                block[o + 35] = (byte)Int(a, "right_deadband_size");
            }
            return block;
        }

        /// <summary>
        /// Sets <paramref name="preset"/>'s dead zones, curves and brake "100% at" from <paramref name="block"/> (call on the
        /// UI thread). The brake keeps the plugin's "Pedal max" (the force the pedals are set up for now); the clutch is
        /// imported only when <paramref name="clutch"/> is on and the block uses it. Calibration is left alone: the plugin's
        /// is for the pedals as they are set up now.
        /// </summary>
        public static Result Apply(byte[] block, PedalPreset preset, bool clutch)
        {
            var result = new Result();
            if (!PedalSettingsLayout.IsLayoutVerified(block))
            {
                result.Lines.Add($"Not imported: the plugin doesn't know this settings layout (firmware {PedalSettingsLayout.FirmwareVersionText(block)}).");
                return result;
            }
            result.Lines.Add($"Imported from firmware {PedalSettingsLayout.FirmwareVersionText(block)} settings:");

            var throttle = PedalSettingsLayout.ReadAxis(block, PedalSettingsLayout.ThrottleAxis(block));
            ApplyTravel(preset.Throttle, throttle, "Throttle", result);

            var brake = PedalSettingsLayout.ReadAxis(block, PedalSettingsLayout.BrakeAxis(block));
            double? unitsPerKg = PedalSettingsLayout.UnitsPerKg(block);
            if (unitsPerKg == null || preset.Brake.FirmwareMaxForceKg <= 0)
                ApplyTravel(preset.Brake, brake, "Brake", result);
            else
                ApplyBrake(preset.Brake, brake, unitsPerKg.Value, result);

            int clutchAxis = PedalSettingsLayout.ClutchAxis(block);
            if (clutch && PedalSettingsLayout.IsAxisUsed(block, clutchAxis))
                ApplyTravel(preset.Clutch, PedalSettingsLayout.ReadAxis(block, clutchAxis), "Clutch", result);
            else if (clutch)
                result.Lines.Add("Clutch: not in use in these settings, left as it was.");

            result.Success = true;
            return result;
        }

        /// <summary>Throttle and clutch: dead zones are where the output ends sit in the raw range, in percent.</summary>
        private static void ApplyTravel(PedalConfig config, PedalSettingsLayout.AxisSettings axis, string name, Result result)
        {
            double span = axis.RawMax - axis.RawMin;
            if (span <= 0 || axis.OutHigh <= axis.OutLow)
            {
                result.Lines.Add($"{name}: no usable range in these settings (output ends {axis.OutLow}…{axis.OutHigh}), left as it was.");
                return;
            }
            SetDeadzone(config, (axis.OutLow - axis.RawMin) / span * 100, (axis.OutHigh - axis.RawMin) / span * 100);
            SetCurve(config, axis);
            result.Lines.Add($"{name}: dead zone {config.DeadzoneLow:0.#}–{config.DeadzoneHigh:0.#} %, {CurveText(axis)}{InvertNote(axis)}");
        }

        /// <summary>
        /// Brake: the Manager's 100 % sits at the output high end, so that force becomes "100% at", and the start dead
        /// zone is the low end's share of it. If the pedals are set up for less force than that, 100 % can't be reached
        /// at the same force; the end dead zone then stays at 100 % and a note says so.
        /// </summary>
        private static void ApplyBrake(PedalConfig config, PedalSettingsLayout.AxisSettings axis, double unitsPerKg, Result result)
        {
            double lowKg = (axis.OutLow - axis.RawMin) / unitsPerKg;
            double highKg = (axis.OutHigh - axis.RawMin) / unitsPerKg;
            if (axis.OutHigh <= axis.OutLow || highKg <= 0)
            {
                ApplyTravel(config, axis, "Brake", result);
                return;
            }
            double maxKg = Math.Min(highKg, config.FirmwareMaxForceKg);
            config.MaxForceKg = Math.Round(maxKg, 1);
            SetDeadzone(config, lowKg / config.MaxForceKg * 100, Math.Min(100, highKg / config.MaxForceKg * 100));
            SetCurve(config, axis);
            result.Lines.Add($"Brake: 100% at {config.MaxForceKg:0.#} kg, dead zone start {config.DeadzoneLow:0.#} %, {CurveText(axis)}{InvertNote(axis)}");
            if (highKg > config.FirmwareMaxForceKg + 0.05)
                result.Lines.Add($"  These settings reach 100 % at {highKg:0.#} kg, but the pedals are set up for {config.FirmwareMaxForceKg:0.#} kg; " +
                                 "raise the pedals' max force with Set up pedals to match exactly.");
        }

        private static void SetDeadzone(PedalConfig config, double low, double high)
        {
            // Each setter keeps low < high, so clear the start first.
            config.DeadzoneLow = 0;
            config.DeadzoneHigh = Math.Round(Math.Max(1, Math.Min(100, high)), 2);
            config.DeadzoneLow = Math.Round(Math.Max(0, Math.Min(99, low)), 2);
        }

        /// <summary>Manager points are −100…100 for 0…100 % output.</summary>
        private static void SetCurve(PedalConfig config, PedalSettingsLayout.AxisSettings axis)
        {
            if (axis.IsLinear)
            {
                config.ApplyShape(CurveShape.Linear);
                return;
            }
            config.CurveShape = CurveShape.Custom;
            config.CurvePoints = axis.Curve.Select(p => (p + 100) / 2.0).ToArray();
        }

        private static string CurveText(PedalSettingsLayout.AxisSettings axis) =>
            axis.IsLinear ? "linear curve" : "curve " + string.Join(" ", axis.Curve.Select(p => ((p + 100) / 2.0).ToString("0.#", CultureInfo.InvariantCulture)));

        // The firmware inverts the output after the curve; the plugin's Invert flips the input, so it isn't carried over.
        private static string InvertNote(PedalSettingsLayout.AxisSettings axis) => axis.Inverted ? " (inverted in the Manager: not imported)" : "";

        private static void WriteInt16(byte[] block, int offset, int value)
        {
            var bytes = BitConverter.GetBytes((short)value);
            block[offset] = bytes[0];
            block[offset + 1] = bytes[1];
        }
    }
}
