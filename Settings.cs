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

    /// <summary>
    /// Throttle overrides applied after shaping: a cut on upshift and a blip on downshift (from the shift paddles, through
    /// SimHub actions), and a cut while the brake is pressed past a level. Per preset, since cars need different amounts.
    /// </summary>
    public class ShiftAssist : Observable
    {
        private bool upshiftCut;
        private double upshiftCutMs = 60;
        private bool downshiftBlip;
        private double blipPercent = 35;
        private double blipMs = 120;
        private bool brakeCut;
        private double brakeCutPercent = 30;

        public bool UpshiftCut { get => upshiftCut; set => Set(ref upshiftCut, value); }

        /// <summary>How long the throttle is held at 0 after an upshift press.</summary>
        public double UpshiftCutMs { get => upshiftCutMs; set => Set(ref upshiftCutMs, Math.Max(10, Math.Min(500, value))); }

        public bool DownshiftBlip { get => downshiftBlip; set => Set(ref downshiftBlip, value); }

        /// <summary>Throttle output during a blip (the pedal still wins if pressed further).</summary>
        public double BlipPercent { get => blipPercent; set => Set(ref blipPercent, Math.Max(1, Math.Min(100, value))); }

        public double BlipMs { get => blipMs; set => Set(ref blipMs, Math.Max(10, Math.Min(500, value))); }

        public bool BrakeCut { get => brakeCut; set => Set(ref brakeCut, value); }

        /// <summary>Brake output (%) from which the throttle is cut to 0.</summary>
        public double BrakeCutPercent { get => brakeCutPercent; set => Set(ref brakeCutPercent, Math.Max(1, Math.Min(100, value))); }

        public ShiftAssist Clone() => JsonConvert.DeserializeObject<ShiftAssist>(JsonConvert.SerializeObject(this));
    }

    public class PedalPreset
    {
        public PedalConfig Throttle { get; set; }
        public PedalConfig Brake { get; set; }

        /// <summary>Used only when <see cref="PluginSettings.ClutchEnabled"/> is on.</summary>
        public PedalConfig Clutch { get; set; }

        public ShiftAssist Assist { get; set; }

        /// <summary>
        /// Pro X Ultra reports throttle on X, brake on Y and clutch on Z; they go to the same vJoy axes.
        /// The brake is a load cell, so it gets max force scaling (starting from 60 kg, the value the pedals shipped with).
        /// </summary>
        public static PedalPreset CreateDefault() => new PedalPreset
        {
            Throttle = new PedalConfig { SourceAxis = InputAxis.X, OutputAxis = VJoyAxis.X },
            Brake = new PedalConfig { SourceAxis = InputAxis.Y, OutputAxis = VJoyAxis.Y, FirmwareMaxForceKg = 60, MaxForceKg = 60 },
            Clutch = new PedalConfig { SourceAxis = InputAxis.Z, OutputAxis = VJoyAxis.Z },
            Assist = new ShiftAssist(),
        };

        public PedalPreset Clone() => new PedalPreset
        {
            Throttle = Throttle.Clone(), Brake = Brake.Clone(), Clutch = Clutch.Clone(), Assist = Assist.Clone(),
        };

        public bool SameAs(PedalPreset other) =>
            other != null && JsonConvert.SerializeObject(this) == JsonConvert.SerializeObject(other);

        public void EnsureValid()
        {
            var defaults = CreateDefault();
            Throttle = Throttle ?? defaults.Throttle;
            Brake = Brake ?? defaults.Brake;
            // Presets from before the clutch: it sits two axes after the throttle (Z on 2.0.x, Rz on 2.1.x).
            if (Clutch == null)
            {
                Clutch = defaults.Clutch;
                if (Throttle.SourceAxis == InputAxis.RotationX) Clutch.SourceAxis = InputAxis.RotationZ;
            }
            Assist = Assist ?? defaults.Assist;
        }
    }

    public class PluginSettings
    {
        public const string DefaultPresetName = "Default";

        /// <summary>SIMSONN Pro X Ultra USB IDs (VID_DDFD, PID_6012).</summary>
        public int VendorId { get; set; } = 0xDDFD;
        public int ProductId { get; set; } = 0x6012;

        public uint VJoyDeviceId { get; set; } = 1;

        /// <summary>A clutch pedal is connected: shape it and feed its vJoy axis too.</summary>
        public bool ClutchEnabled { get; set; }

        /// <summary>Simsonn Manager install folder chosen by hand, used for firmware files; null to find it automatically.</summary>
        public string ManagerFolder { get; set; }

        /// <summary>
        /// Switch preset automatically: the one saved for the current car, else its class, else the preset named after
        /// the game, else Default.
        /// </summary>
        public bool AutoApplyPerGame { get; set; } = true;

        /// <summary>Preset saved for a car, keyed by <see cref="CarKey"/> ("game|car id").</summary>
        public Dictionary<string, string> CarPresets { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Preset saved for a car class, keyed by <see cref="CarKey"/> ("game|class").</summary>
        public Dictionary<string, string> ClassPresets { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Key for <see cref="CarPresets"/> / <see cref="ClassPresets"/>: car ids and class names are only unique within a game.</summary>
        public static string CarKey(string game, string carOrClass) => $"{game}|{carOrClass}";

        public string ActivePresetName { get; set; } = DefaultPresetName;

        /// <summary>Presets as edited; this is what drives the pedals. Autosaved, so edits survive a restart.</summary>
        public Dictionary<string, PedalPreset> Presets { get; set; } =
            new Dictionary<string, PedalPreset>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each preset as of its last Save; Revert goes back to these.</summary>
        public Dictionary<string, PedalPreset> SavedPresets { get; set; } =
            new Dictionary<string, PedalPreset>(StringComparer.OrdinalIgnoreCase);

        public void EnsureValid()
        {
            // Json.NET may hand back a dictionary without the case-insensitive comparer.
            Presets = (Presets ?? new Dictionary<string, PedalPreset>())
                .Where(p => p.Value != null)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var preset in Presets.Values) preset.EnsureValid();
            if (!Presets.ContainsKey(DefaultPresetName)) Presets[DefaultPresetName] = PedalPreset.CreateDefault();

            // Settings from before Save/Revert have no saved copies: treat what's there as saved.
            SavedPresets = (SavedPresets ?? new Dictionary<string, PedalPreset>())
                .Where(p => p.Value != null && Presets.ContainsKey(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var preset in SavedPresets.Values) preset.EnsureValid();
            foreach (var name in Presets.Keys.Where(n => !SavedPresets.ContainsKey(n)).ToList())
                SavedPresets[name] = Presets[name].Clone();
            // Car and class links: case-insensitive, and only to presets that still exist.
            Dictionary<string, string> Links(Dictionary<string, string> links) => (links ?? new Dictionary<string, string>())
                .Where(p => p.Value != null && Presets.ContainsKey(p.Value))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            CarPresets = Links(CarPresets);
            ClassPresets = Links(ClassPresets);

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
