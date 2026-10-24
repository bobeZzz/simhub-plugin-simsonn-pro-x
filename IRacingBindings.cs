using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpDX.DirectInput;

namespace SimsonnPedals
{
    /// <summary>
    /// Reads iRacing's pedal bindings (Documents\iRacing\controls.cfg, with device and axis names from joyCalib.yaml) and
    /// says whether throttle, brake and clutch are bound to the plugin's vJoy axes.
    /// controls.cfg is binary: each control is its name, a 0 byte, 4 zero bytes, int32 0x1F, int32 bound (1/0),
    /// int32 axis index, int32, then the DirectInput instance GUID of the device (seen in iRacing's file from 2024).
    /// </summary>
    public static class IRacingBindings
    {
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing");
        public static string ControlsPath => Path.Combine(Folder, "controls.cfg");
        public static string CalibrationPath => Path.Combine(Folder, "joyCalib.yaml");

        public sealed class Binding
        {
            public string Control { get; set; }
            public bool Bound { get; set; }
            public int Axis { get; set; }
            public Guid Device { get; set; }
        }

        /// <summary>The binding of <paramref name="control"/> ("Throttle", "Brake", "Clutch"), or null if the file doesn't have it.</summary>
        public static Binding Find(byte[] controls, string control)
        {
            byte[] name = Encoding.ASCII.GetBytes(control + "\0\0\0\0\0");
            for (int i = 1; i + name.Length + 32 <= controls.Length; i++)
            {
                // A letter or digit before it means a longer name that merely ends the same way.
                if (!Matches(controls, i, name) || IsNameChar(controls[i - 1])) continue;
                int o = i + name.Length;
                if (BitConverter.ToInt32(controls, o) != 0x1F) continue;
                var guid = new byte[16];
                Array.Copy(controls, o + 16, guid, 0, 16);
                return new Binding
                {
                    Control = control,
                    Bound = BitConverter.ToInt32(controls, o + 4) == 1,
                    Axis = BitConverter.ToInt32(controls, o + 8),
                    Device = new Guid(guid),
                };
            }
            return null;
        }

        private static bool IsNameChar(byte b) => b >= '0' && b <= '9' || b >= 'A' && b <= 'Z' || b >= 'a' && b <= 'z';

        private static bool Matches(byte[] data, int at, byte[] pattern)
        {
            for (int j = 0; j < pattern.Length; j++)
                if (data[at + j] != pattern[j]) return false;
            return true;
        }

        /// <summary>Device and axis names from joyCalib.yaml: GUID → (device name, axis index → axis name).</summary>
        public static Dictionary<Guid, (string Name, Dictionary<int, string> Axes)> ReadCalibration(string[] lines)
        {
            var result = new Dictionary<Guid, (string, Dictionary<int, string>)>();
            string device = null;
            Dictionary<int, string> axes = null;
            int? axis = null;
            foreach (var raw in lines)
            {
                string line = raw.Trim().TrimStart('-').Trim();
                string Value() => line.Substring(line.IndexOf(':') + 1).Trim().Trim('\'');
                if (line.StartsWith("DeviceName:")) { device = Value(); axes = new Dictionary<int, string>(); }
                else if (line.StartsWith("InstanceGUID:") && Guid.TryParse(Value(), out var guid) && axes != null) result[guid] = (device, axes);
                else if (line.StartsWith("Axis:") && int.TryParse(Value(), out int a)) axis = a;
                else if (line.StartsWith("AxisName:") && axis != null && axes != null) axes[axis.Value] = Value();
            }
            return result;
        }

        /// <summary>DirectInput's name for each vJoy axis, as iRacing shows it.</summary>
        private static string VJoyAxisName(VJoyAxis axis)
        {
            switch (axis)
            {
                case VJoyAxis.X: return "X Axis";
                case VJoyAxis.Y: return "Y Axis";
                case VJoyAxis.Z: return "Z Axis";
                case VJoyAxis.RX: return "X Rotation";
                case VJoyAxis.RY: return "Y Rotation";
                case VJoyAxis.RZ: return "Z Rotation";
                default: return "Slider";
            }
        }

        private static readonly HashSet<string> KnownAxisNames = new HashSet<string>(
            Enum.GetValues(typeof(VJoyAxis)).Cast<VJoyAxis>().Select(VJoyAxisName), StringComparer.OrdinalIgnoreCase);

        public sealed class Result
        {
            /// <summary>True when every checked pedal is bound to a vJoy device on the expected axis (as far as the files tell).</summary>
            public bool Ok { get; set; }
            public string Text { get; set; }
        }

        /// <summary>
        /// Checks the bindings against the active preset. Devices are identified through DirectInput (vJoy is VID 1234 /
        /// PID BEAD) and, for devices not plugged in, by the names iRacing saved in joyCalib.yaml.
        /// </summary>
        public static Result Check(PedalPreset preset, bool clutch, int pedalsVendorId)
        {
            if (!File.Exists(ControlsPath)) return new Result { Ok = true, Text = "iRacing: no controls.cfg in " + Folder + " (not installed, or never run)." };
            var controls = File.ReadAllBytes(ControlsPath);
            var calibration = File.Exists(CalibrationPath) ? ReadCalibration(File.ReadAllLines(CalibrationPath)) : new Dictionary<Guid, (string, Dictionary<int, string>)>();
            var attached = new Dictionary<Guid, DeviceInstance>();
            using (var directInput = new DirectInput())
                foreach (var d in directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
                    attached[d.InstanceGuid] = d;
            return Check(controls, calibration, attached.ToDictionary(p => p.Key, p => (VendorProduct(p.Value.ProductGuid), p.Value.ProductName.Trim())),
                preset, clutch, pedalsVendorId);
        }

        private const uint VJoyVendorProduct = 0xBEAD1234;

        private static uint VendorProduct(Guid productGuid) => BitConverter.ToUInt32(productGuid.ToByteArray(), 0);

        /// <summary>The check itself, on plain data so it can be tested. <paramref name="attached"/>: instance GUID → (PIDVID, name).</summary>
        public static Result Check(byte[] controls, Dictionary<Guid, (string Name, Dictionary<int, string> Axes)> calibration,
            Dictionary<Guid, (uint VendorProduct, string Name)> attached, PedalPreset preset, bool clutch, int pedalsVendorId)
        {
            var pedals = new List<(string Control, string Label, PedalConfig Config)> { ("Throttle", "throttle", preset.Throttle), ("Brake", "brake", preset.Brake) };
            if (clutch) pedals.Add(("Clutch", "clutch", preset.Clutch));

            var problems = new List<string>();
            var good = new List<string>();
            foreach (var (control, label, config) in pedals)
            {
                var binding = Find(controls, control);
                if (binding == null) { problems.Add($"{label}: not found in controls.cfg"); continue; }
                if (!binding.Bound) { problems.Add($"{label} is not bound"); continue; }

                calibration.TryGetValue(binding.Device, out var calib);
                string axisName = calib.Axes != null && calib.Axes.TryGetValue(binding.Axis, out var n) ? n : null;
                bool known = attached.TryGetValue(binding.Device, out var device);
                string deviceName = known ? device.Name : calib.Name ?? "a device that isn't plugged in";
                bool isVJoy = known ? device.VendorProduct == VJoyVendorProduct : (calib.Name ?? "").IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isPedals = known && (device.VendorProduct & 0xFFFF) == pedalsVendorId;

                string where = axisName == null ? $"axis {binding.Axis}" : axisName;
                if (isPedals)
                    problems.Add($"{label} is bound to the pedals directly ({where}), so the plugin's settings are skipped; bind it to vJoy");
                else if (!isVJoy)
                    problems.Add($"{label} is bound to {deviceName} ({where}), not vJoy");
                // Only names this check knows are compared, so an unexpected naming never raises a false alarm.
                else if (axisName != null && KnownAxisNames.Contains(axisName) && !string.Equals(axisName, VJoyAxisName(config.OutputAxis), StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{label} is bound to vJoy {axisName}, but the plugin sends it on {VJoyAxisName(config.OutputAxis)}");
                else
                    good.Add(label);
            }

            if (problems.Count == 0)
                return new Result { Ok = true, Text = $"iRacing: {string.Join(", ", good)} bound to vJoy." };
            var text = "iRacing: " + string.Join("; ", problems) + ".";
            if (good.Count > 0) text += $" OK: {string.Join(", ", good)}.";
            return new Result { Ok = false, Text = text };
        }
    }
}
