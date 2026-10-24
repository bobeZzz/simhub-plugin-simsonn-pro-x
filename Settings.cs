using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace SimsonnPedals
{
    /// <summary>DirectInput axis of the physical pedal base that feeds a pedal.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum InputAxis { X, Y, Z, RotationX, RotationY, RotationZ, Slider0, Slider1 }

    /// <summary>vJoy output axis; values are the HID usages vJoy expects.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum VJoyAxis : uint { X = 0x30, Y = 0x31, Z = 0x32, RX = 0x33, RY = 0x34, RZ = 0x35, Slider0 = 0x36, Slider1 = 0x37 }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum CurveShape { Linear, Regressive, Progressive, SCurve, InverseS, Custom }

    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>Settings for one pedal. Edited live from the UI and read by the input thread.</summary>
    public class PedalConfig : Observable
    {
        public const int CurvePointCount = 11;
        public const int RawMax = 65535;

        private InputAxis sourceAxis;
        private bool invert;
        private int calibrationMin;
        private int calibrationMax = RawMax;
        private double deadzoneLow;
        private double deadzoneHigh = 100;
        private CurveShape curveShape = CurveShape.Linear;
        private double[] curvePoints = CurvePresets.Get(CurveShape.Linear);
        private VJoyAxis outputAxis;
        private double firmwareMaxForceKg;
        private double maxForceKg;

        public InputAxis SourceAxis { get => sourceAxis; set => Set(ref sourceAxis, value); }
        public bool Invert { get => invert; set => Set(ref invert, value); }
        public int CalibrationMin { get => calibrationMin; set => Set(ref calibrationMin, Clamp(value, 0, RawMax)); }
        public int CalibrationMax { get => calibrationMax; set => Set(ref calibrationMax, Clamp(value, 0, RawMax)); }

        /// <summary>Percent of travel (after calibration) treated as 0% output.</summary>
        public double DeadzoneLow { get => deadzoneLow; set => Set(ref deadzoneLow, Math.Max(0, Math.Min(value, deadzoneHigh - 1))); }

        /// <summary>Percent of travel (after calibration) at which output reaches 100%.</summary>
        public double DeadzoneHigh { get => deadzoneHigh; set => Set(ref deadzoneHigh, Math.Min(100, Math.Max(value, deadzoneLow + 1))); }

        public CurveShape CurveShape { get => curveShape; set => Set(ref curveShape, value); }

        /// <summary>Output percent (0-100) at input 0%, 10%, ..., 100%. Treated as immutable: replace, don't mutate.</summary>
        public double[] CurvePoints
        {
            get => curvePoints;
            set => Set(ref curvePoints, NormalizeCurve(value));
        }

        public VJoyAxis OutputAxis { get => outputAxis; set => Set(ref outputAxis, value); }

        /// <summary>Max force set in Simsonn Manager, i.e. the force at which the pedal reports 100%. 0 disables force scaling.</summary>
        public double FirmwareMaxForceKg
        {
            get => firmwareMaxForceKg;
            set { if (Set(ref firmwareMaxForceKg, Math.Max(0, Math.Min(500, value)))) Raise(nameof(HasForceScaling)); }
        }

        /// <summary>Force that should produce 100% output. Capped at the firmware value, where the axis saturates.</summary>
        public double MaxForceKg
        {
            get => maxForceKg;
            set { if (Set(ref maxForceKg, Math.Max(0, Math.Min(500, value)))) Raise(nameof(HasForceScaling)); }
        }

        [JsonIgnore]
        public bool HasForceScaling => firmwareMaxForceKg > 0 && maxForceKg > 0;

        public void ApplyShape(CurveShape shape)
        {
            CurveShape = shape;
            if (shape != CurveShape.Custom) CurvePoints = CurvePresets.Get(shape);
        }

        /// <summary>Moves one curve point and switches the shape to Custom.</summary>
        public void SetCurvePoint(int index, double outputPercent)
        {
            var copy = (double[])curvePoints.Clone();
            copy[index] = Math.Max(0, Math.Min(100, outputPercent));
            CurveShape = CurveShape.Custom;
            CurvePoints = copy;
        }

        public void ResetCalibration()
        {
            CalibrationMin = 0;
            CalibrationMax = RawMax;
        }

        public PedalConfig Clone() => JsonConvert.DeserializeObject<PedalConfig>(JsonConvert.SerializeObject(this));

        private static double[] NormalizeCurve(double[] points)
        {
            if (points == null || points.Length != CurvePointCount) return CurvePresets.Get(CurveShape.Linear);
            return points.Select(p => double.IsNaN(p) ? 0 : Math.Max(0, Math.Min(100, p))).ToArray();
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
    }

    public class PedalPreset
    {
        public PedalConfig Throttle { get; set; }
        public PedalConfig Brake { get; set; }

        /// <summary>
        /// Pro X Ultra reports throttle on X and brake on Y; they go to the same vJoy axes.
        /// The brake is a load cell, so it gets max force scaling (starting from 60 kg, the value the pedals shipped with).
        /// </summary>
        public static PedalPreset CreateDefault() => new PedalPreset
        {
            Throttle = new PedalConfig { SourceAxis = InputAxis.X, OutputAxis = VJoyAxis.X },
            Brake = new PedalConfig { SourceAxis = InputAxis.Y, OutputAxis = VJoyAxis.Y, FirmwareMaxForceKg = 60, MaxForceKg = 60 },
        };

        public PedalPreset Clone() => new PedalPreset { Throttle = Throttle.Clone(), Brake = Brake.Clone() };

        public void EnsureValid()
        {
            var defaults = CreateDefault();
            Throttle = Throttle ?? defaults.Throttle;
            Brake = Brake ?? defaults.Brake;
        }
    }

    public class PluginSettings
    {
        public const string DefaultPresetName = "Default";

        /// <summary>SIMSONN Pro X Ultra USB IDs (VID_DDFD, PID_6012).</summary>
        public int VendorId { get; set; } = 0xDDFD;
        public int ProductId { get; set; } = 0x6012;

        public uint VJoyDeviceId { get; set; } = 1;

        /// <summary>Switch to the preset named after the running game when SimHub detects it.</summary>
        public bool AutoApplyPerGame { get; set; } = true;

        public string ActivePresetName { get; set; } = DefaultPresetName;

        public Dictionary<string, PedalPreset> Presets { get; set; } =
            new Dictionary<string, PedalPreset>(StringComparer.OrdinalIgnoreCase);

        public void EnsureValid()
        {
            // Json.NET may hand back a dictionary without the case-insensitive comparer.
            Presets = (Presets ?? new Dictionary<string, PedalPreset>())
                .Where(p => p.Value != null)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var preset in Presets.Values) preset.EnsureValid();
            if (!Presets.ContainsKey(DefaultPresetName)) Presets[DefaultPresetName] = PedalPreset.CreateDefault();
            if (string.IsNullOrEmpty(ActivePresetName) || !Presets.ContainsKey(ActivePresetName)) ActivePresetName = DefaultPresetName;
            if (VJoyDeviceId < 1 || VJoyDeviceId > 16) VJoyDeviceId = 1;
        }
    }

    public static class CurvePresets
    {
        public static double[] Get(CurveShape shape)
        {
            Func<double, double> f;
            switch (shape)
            {
                case CurveShape.Regressive: f = x => Math.Pow(x, 0.55); break;
                case CurveShape.Progressive: f = x => Math.Pow(x, 1.8); break;
                case CurveShape.SCurve: f = x => x * x * (3 - 2 * x); break;
                case CurveShape.InverseS: f = x => 0.5 + Math.Asin(2 * x - 1) / Math.PI; break;
                default: f = x => x; break;
            }

            var points = new double[PedalConfig.CurvePointCount];
            for (int i = 0; i < points.Length; i++)
                points[i] = Math.Round(f(i / 10.0) * 100, 1);
            return points;
        }
    }
}
