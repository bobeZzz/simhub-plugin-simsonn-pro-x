using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;

namespace SimsonnPedals
{
    [PluginDescription("Dead zones, response curves and calibration for the SIMSONN Pro X Ultra throttle and brake, output through vJoy.")]
    [PluginAuthor("bobeZzz")]
    [PluginName("SIMSONN Pedals")]
    public class SimsonnPedalsPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        private const string SettingsKey = "Settings";
        private const int AutosaveDelayMs = 2000;

        // Presets are edited on the UI thread and looked up on SimHub's data thread.
        private readonly object presetLock = new object();
        private readonly Dictionary<string, bool> modifiedCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private string lastGameName;
        private Timer autosaveTimer;

        public PluginManager PluginManager { get; set; }

        public PluginSettings Settings { get; private set; }

        public PedalEngine Engine { get; private set; }

        /// <summary>Game SimHub currently has selected, or null before the first update.</summary>
        public string CurrentGameName => lastGameName;

        /// <summary>True while SimHub sees the game running (not just selected).</summary>
        public bool IsGameRunning { get; private set; }

        /// <summary>Raised (on any thread) when the active preset is swapped.</summary>
        public event Action ActivePresetChanged;

        public string LeftMenuTitle => "SIMSONN Pedals";

        public ImageSource PictureIcon => PluginIcon.Value;

        public PedalPreset ActivePreset => Settings.Presets[Settings.ActivePresetName];

        /// <summary>Result of the last update check this session, or null if none has run.</summary>
        public ReleaseInfo LatestRelease { get; set; }

        /// <summary>Version installed by the updater this session, waiting for a SimHub restart.</summary>
        public Version PendingUpdateVersion { get; set; }

        public void Init(PluginManager pluginManager)
        {
            PluginUpdater.CleanUpOldFiles();
            Settings = this.ReadCommonSettings(SettingsKey, () => new PluginSettings());
            Settings.EnsureValid();
            autosaveTimer = new Timer(_ => OnAutosaveDue(), null, Timeout.Infinite, Timeout.Infinite);
            foreach (var preset in Settings.Presets.Values) Track(preset);

            Engine = new PedalEngine(() => Settings) { ActivePreset = ActivePreset };
            Engine.Start();
            // Backup for exits that skip End(): vJoy keeps its last values, so the pedals must be zeroed before we go.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            this.AttachDelegate("DeviceConnected", () => Engine.DeviceConnected);
            this.AttachDelegate("VJoyStatus", () => Engine.VJoyStatus);
            this.AttachDelegate("ActivePreset", () => Settings.ActivePresetName);
            this.AttachDelegate("Throttle.Raw", () => Engine.Throttle.Raw);
            this.AttachDelegate("Throttle.Position", () => Math.Round(Engine.Throttle.Position * 100, 1));
            this.AttachDelegate("Throttle.Output", () => Math.Round(Engine.Throttle.Output * 100, 1));
            this.AttachDelegate("Brake.Raw", () => Engine.Brake.Raw);
            this.AttachDelegate("Brake.Position", () => Math.Round(Engine.Brake.Position * 100, 1));
            this.AttachDelegate("Brake.Output", () => Math.Round(Engine.Brake.Output * 100, 1));
            this.AttachDelegate("Brake.ForceKg", () => Math.Round(Engine.Brake.Position * ActivePreset.Brake.FirmwareMaxForceKg, 1));

            this.AddAction("NextPreset", (pm, input) => CyclePreset());
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            IsGameRunning = data.GameRunning;
            var game = data.GameName;
            if (game == lastGameName) return;
            lastGameName = game;
            if (Settings.AutoApplyPerGame) ApplyPresetForGame(game);
        }

        private void OnProcessExit(object sender, EventArgs e) => Engine?.Dispose();

        public void End(PluginManager pluginManager)
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            Engine?.Dispose();
            autosaveTimer?.Dispose();
            Save();
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager) =>
            new UI.SettingsControl(this);

        public void Save() => this.SaveCommonSettings(SettingsKey, Settings);

        /// <summary>Saves a couple of seconds after the last change, so a SimHub crash loses at most that much.</summary>
        public void RequestSave() => autosaveTimer?.Change(AutosaveDelayMs, Timeout.Infinite);

        private void OnAutosaveDue()
        {
            // Edits happen on the UI thread; serialize there so a half-made edit is never written.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null) dispatcher.BeginInvoke(new Action(SafeSave));
            else SafeSave();
        }

        private void SafeSave()
        {
            try
            {
                Save();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[SimsonnPedals] Autosave failed", ex);
            }
        }

        private void Track(PedalPreset preset)
        {
            preset.Throttle.PropertyChanged += OnPresetEdited;
            preset.Brake.PropertyChanged += OnPresetEdited;
        }

        private void Untrack(PedalPreset preset)
        {
            preset.Throttle.PropertyChanged -= OnPresetEdited;
            preset.Brake.PropertyChanged -= OnPresetEdited;
        }

        private void OnPresetEdited(object sender, PropertyChangedEventArgs e)
        {
            lock (presetLock) modifiedCache.Clear();
            RequestSave();
        }

        /// <summary>True if <paramref name="name"/> has edits since its last Save. Cached until the next edit.</summary>
        public bool IsModified(string name)
        {
            lock (presetLock)
            {
                if (name == null || !Settings.Presets.TryGetValue(name, out var preset)) return false;
                if (!modifiedCache.TryGetValue(name, out bool modified))
                {
                    Settings.SavedPresets.TryGetValue(name, out var saved);
                    modifiedCache[name] = modified = !preset.SameAs(saved);
                }
                return modified;
            }
        }

        /// <summary>Makes the active preset's current values the ones Revert goes back to.</summary>
        public void SaveActivePreset()
        {
            lock (presetLock)
            {
                Settings.SavedPresets[Settings.ActivePresetName] = ActivePreset.Clone();
                modifiedCache.Clear();
            }
            Save();
        }

        /// <summary>Puts the active preset back to its last Save.</summary>
        public void RevertActivePreset()
        {
            string name = Settings.ActivePresetName;
            lock (presetLock)
            {
                if (!Settings.SavedPresets.TryGetValue(name, out var saved)) return;
                Restore(name, saved);
            }
            ActivatePreset(name);
            Save();
        }

        // Caller holds presetLock. Swaps in a fresh copy, so the engine and the page pick it up through ActivatePreset.
        private void Restore(string name, PedalPreset saved)
        {
            if (Settings.Presets.TryGetValue(name, out var old)) Untrack(old);
            var copy = saved.Clone();
            Track(copy);
            Settings.Presets[name] = copy;
            modifiedCache.Clear();
        }

        public void ApplyPresetForGame(string game)
        {
            lock (presetLock)
                ActivatePreset(!string.IsNullOrEmpty(game) && Settings.Presets.ContainsKey(game) ? game : PluginSettings.DefaultPresetName);
        }

        public void ActivatePreset(string name)
        {
            lock (presetLock)
            {
                if (!Settings.Presets.TryGetValue(name, out var preset)) return;
                if (string.Equals(Settings.ActivePresetName, name, StringComparison.OrdinalIgnoreCase) && Engine.ActivePreset == preset) return;
                Settings.ActivePresetName = name;
                Engine.ActivePreset = preset;
            }
            SimHub.Logging.Current.Info($"[SimsonnPedals] Active preset: {name}");
            ActivePresetChanged?.Invoke();
        }

        /// <summary>
        /// Saves the active preset's current values under <paramref name="name"/> and switches to it. Like "Save as"
        /// in an editor, the original preset goes back to its last Save: its edits now live in the new one.
        /// </summary>
        public void SaveActiveAs(string name)
        {
            string source = Settings.ActivePresetName;
            if (string.Equals(source, name, StringComparison.OrdinalIgnoreCase))
            {
                SaveActivePreset();
                return;
            }

            var copy = ActivePreset.Clone();
            Track(copy);
            lock (presetLock)
            {
                if (Settings.Presets.TryGetValue(name, out var replaced)) Untrack(replaced);
                Settings.Presets[name] = copy;
                Settings.SavedPresets[name] = copy.Clone();
                if (Settings.SavedPresets.TryGetValue(source, out var sourceSaved)) Restore(source, sourceSaved);
                modifiedCache.Clear();
            }
            ActivatePreset(name);
            Save();
        }

        /// <summary>Records the max force now stored in the pedals in every preset (call on the UI thread).</summary>
        public void SetPedalMaxForce(double kg)
        {
            // A fact about the pedals, not an edit: update the saved copies too, so presets don't show as unsaved.
            foreach (var preset in Settings.Presets.Values.Concat(Settings.SavedPresets.Values)) preset.Brake.FirmwareMaxForceKg = kg;
            lock (presetLock) modifiedCache.Clear();
        }

        /// <summary>
        /// After calibrating the pedals themselves they report 0–100 % over the full axis, so the plugin's own
        /// calibration in every preset goes back to the full range (call on the UI thread).
        /// </summary>
        public void ResetPluginCalibration()
        {
            foreach (var preset in Settings.Presets.Values.Concat(Settings.SavedPresets.Values))
            {
                preset.Throttle.ResetCalibration();
                preset.Brake.ResetCalibration();
            }
            lock (presetLock) modifiedCache.Clear();
            Save();
        }

        public bool DeletePreset(string name)
        {
            if (string.Equals(name, PluginSettings.DefaultPresetName, StringComparison.OrdinalIgnoreCase)) return false;
            lock (presetLock)
            {
                if (!Settings.Presets.TryGetValue(name, out var preset)) return false;
                Untrack(preset);
                Settings.Presets.Remove(name);
                Settings.SavedPresets.Remove(name);
                modifiedCache.Clear();
                if (string.Equals(Settings.ActivePresetName, name, StringComparison.OrdinalIgnoreCase)) Settings.ActivePresetName = null;
            }
            if (Settings.ActivePresetName == null) ActivatePreset(PluginSettings.DefaultPresetName);
            Save();
            return true;
        }

        private void CyclePreset()
        {
            var names = Settings.Presets.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            int i = names.FindIndex(n => string.Equals(n, Settings.ActivePresetName, StringComparison.OrdinalIgnoreCase));
            ActivatePreset(names[(i + 1) % names.Count]);
        }

        private static readonly Lazy<ImageSource> PluginIcon = new Lazy<ImageSource>(() =>
        {
            // Three pedal outlines, drawn so it works on SimHub's black-and-white menu.
            var pen = new Pen(Brushes.White, 1.6);
            var group = new GeometryGroup();
            group.Children.Add(new RectangleGeometry(new Rect(3, 4, 4, 12), 1, 1));
            group.Children.Add(new RectangleGeometry(new Rect(10, 4, 4, 12), 1, 1));
            group.Children.Add(new RectangleGeometry(new Rect(17, 4, 4, 12), 1, 1));
            group.Children.Add(new LineGeometry(new Point(2, 20), new Point(22, 20)));
            var image = new DrawingImage(new GeometryDrawing(null, pen, group));
            image.Freeze();
            return image;
        });
    }
}
