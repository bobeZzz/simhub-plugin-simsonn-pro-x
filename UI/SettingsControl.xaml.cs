using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SimsonnPedals.UI
{
    public partial class SettingsControl : UserControl
    {
        private readonly SimsonnPedalsPlugin plugin;
        private bool updatingPresetList;

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(SimsonnPedalsPlugin plugin) : this()
        {
            this.plugin = plugin;
            BrakePanel.Processor = plugin.Engine.Brake;
            ThrottlePanel.Processor = plugin.Engine.Throttle;
            BrakePanel.Engine = plugin.Engine;
            BrakePanel.SweepKind = SweepPedal.Brake;
            ThrottlePanel.Engine = plugin.Engine;
            ThrottlePanel.SweepKind = SweepPedal.Throttle;
            ClutchPanel.Processor = plugin.Engine.Clutch;
            ClutchPanel.Engine = plugin.Engine;
            ClutchPanel.SweepKind = SweepPedal.Clutch;
            ClutchToggle.IsChecked = plugin.Settings.ClutchEnabled;
            ShowClutch();

            VJoyDeviceCombo.ItemsSource = Enumerable.Range(1, 16).Select(i => (uint)i).ToList();
            VJoyDeviceCombo.SelectedItem = plugin.Settings.VJoyDeviceId;
            AutoApplyToggle.IsChecked = plugin.Settings.AutoApplyPerGame;
            double pedalMax = plugin.ActivePreset.Brake.FirmwareMaxForceKg;
            SetupMaxForceBox.Text = (pedalMax > 0 ? pedalMax : 100).ToString("0.#");

            BindActivePreset();

            // Refresh once per rendered frame rather than on a low-priority timer, so the bars keep up with the pedal.
            Loaded += (s, e) =>
            {
                plugin.ActivePresetChanged += OnActivePresetChanged;
                BindActivePreset();
                CompositionTarget.Rendering += OnRendering;
                ShowUpdateState();
                // One automatic check per SimHub session, the first time this page opens.
                if (plugin.LatestRelease == null && plugin.PendingUpdateVersion == null) CheckForUpdates(quiet: true);
            };
            Unloaded += (s, e) =>
            {
                plugin.ActivePresetChanged -= OnActivePresetChanged;
                CompositionTarget.Rendering -= OnRendering;
                if (rawMonitor != null) StopCalibration();
            };
        }

        private void OnRendering(object sender, EventArgs e) => RefreshLive();

        private void OnActivePresetChanged() => Dispatcher.BeginInvoke(new Action(BindActivePreset));

        private void BindActivePreset()
        {
            var preset = plugin.ActivePreset;
            BrakePanel.Config = preset.Brake;
            ThrottlePanel.Config = preset.Throttle;
            ClutchPanel.Config = preset.Clutch;
            AssistSection.DataContext = preset.Assist;

            updatingPresetList = true;
            PresetCombo.ItemsSource = plugin.Settings.Presets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            PresetCombo.SelectedItem = PresetCombo.Items.Cast<string>()
                .FirstOrDefault(k => string.Equals(k, plugin.Settings.ActivePresetName, StringComparison.OrdinalIgnoreCase));
            updatingPresetList = false;

            DeleteButton.IsEnabled = !string.Equals(plugin.Settings.ActivePresetName, PluginSettings.DefaultPresetName, StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshLive()
        {
            var engine = plugin.Engine;
            DeviceStatusText.Text = engine.DeviceConnected
                ? $"Pedals: {engine.DeviceName} connected ({engine.UpdateRate:0} updates/s)"
                : $"Pedals: not found (looking for VID_{plugin.Settings.VendorId:X4} PID_{plugin.Settings.ProductId:X4})";
            VJoyStatusText.Text = "vJoy: " + engine.VJoyStatus;
            RawAxesText.Text = engine.DeviceConnected ? "Raw axes: " + engine.RawAxes : "";

            var game = plugin.CurrentGameName;
            SaveForGameButton.IsEnabled = !string.IsNullOrEmpty(game);
            SaveForGameButton.Content = string.IsNullOrEmpty(game) ? "Save for current game" : $"Save for {game}";
            string car = plugin.CurrentCarModel, carClass = plugin.CurrentCarClass;
            SaveForCarButton.IsEnabled = !string.IsNullOrEmpty(game) && plugin.CurrentCarId != null;
            SaveForCarButton.Content = SaveForCarButton.IsEnabled ? $"Save for {car}" : "Save for this car";
            SaveForClassButton.IsEnabled = !string.IsNullOrEmpty(game) && carClass != null && plugin.CurrentCarClassKey != null;
            SaveForClassButton.Content = SaveForClassButton.IsEnabled ? $"Save for {SimsonnPedalsPlugin.ClassPresetName(carClass)}" : "Save for this class";
            string autoText = DescribeAutoPreset();
            if (AutoPresetText.Text != autoText) AutoPresetText.Text = autoText;

            RefreshModified();
            RefreshBindingCheck(force: false);
            BrakePanel.Refresh(engine.DeviceConnected);
            ThrottlePanel.Refresh(engine.DeviceConnected);
            if (plugin.Settings.ClutchEnabled) ClutchPanel.Refresh(engine.DeviceConnected);
            RefreshAssist(engine);
        }

        private ThrottleOverride lastOverride;
        private DateTime lastOverrideUntil;

        private void RefreshAssist(PedalEngine engine)
        {
            string paddles = $"Paddle presses seen: {engine.UpshiftCount} up, {engine.DownshiftCount} down";
            if (PaddleStatus.Text != paddles) PaddleStatus.Text = paddles;

            // A cut or blip lasts a few frames; keep its name up for a second so it can be seen.
            var state = engine.Override;
            if (state != ThrottleOverride.None)
            {
                lastOverride = state;
                lastOverrideUntil = DateTime.UtcNow.AddSeconds(1);
            }
            string label = DateTime.UtcNow < lastOverrideUntil ? OverrideName(lastOverride) : "";
            string text = $"Throttle sent {engine.ThrottleSent * 100,5:0.0} %  {label}";
            if (ThrottleSentText.Text != text) ThrottleSentText.Text = text;
        }

        private static string OverrideName(ThrottleOverride state)
        {
            switch (state)
            {
                case ThrottleOverride.ShiftCut: return "upshift cut";
                case ThrottleOverride.Blip: return "blip";
                case ThrottleOverride.BrakeCut: return "brake cut";
                default: return "";
            }
        }

        private void OnTestUpshiftClick(object sender, RoutedEventArgs e) => plugin.Engine.Upshift();

        private void OnTestDownshiftClick(object sender, RoutedEventArgs e) => plugin.Engine.Downshift();

        private void OnImportManagerFileClick(object sender, RoutedEventArgs e)
        {
            string manager = plugin.Settings.ManagerFolder ?? FirmwareFile.FindManagerFolder();
            string devices = manager == null ? null : Path.Combine(manager, "etc", "device");
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Simsonn Manager pedal preset",
                Filter = "Manager presets (*.cfg)|*.cfg|All files (*.*)|*.*",
                InitialDirectory = devices != null && Directory.Exists(devices) ? devices : manager,
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                ShowImport(ManagerImport.Apply(ManagerImport.BlockFromIni(File.ReadAllLines(dialog.FileName)),
                    plugin.ActivePreset, plugin.Settings.ClutchEnabled), Path.GetFileName(Path.GetDirectoryName(dialog.FileName)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                ImportStatus.Text = "Not imported: " + ex.Message;
            }
        }

        private async void OnImportPedalsClick(object sender, RoutedEventArgs e)
        {
            if (plugin.IsGameRunning &&
                MessageBox.Show("A game is running. The pedals may stop responding for a few seconds. Continue?",
                    "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            ImportPedalsButton.IsEnabled = false;
            ImportStatus.Text = "Reading settings from the pedals…";
            try
            {
                int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
                var read = await Task.Run(() => PedalConfigReader.Read(vid, pid, "import"));
                if (!read.Success) ImportStatus.Text = "Not imported: " + read.Summary;
                else ShowImport(ManagerImport.Apply(read.Data, plugin.ActivePreset, plugin.Settings.ClutchEnabled), "the pedals");
            }
            catch (Exception ex)
            {
                ImportStatus.Text = "Not imported: " + ex.Message;
            }
            finally
            {
                ImportPedalsButton.IsEnabled = true;
            }
        }

        private void ShowImport(ManagerImport.Result result, string source)
        {
            ImportStatus.Text = result.Success
                ? $"Into preset {plugin.Settings.ActivePresetName}, from {source}. {result} Save keeps it, Revert undoes it."
                : result.ToString();
        }

        private DateTime nextBindingCheck;
        private string checkedBindingsFor;
        private bool bindingCheckRunning;

        private void OnCheckBindingsClick(object sender, RoutedEventArgs e) => RefreshBindingCheck(force: true);

        /// <summary>
        /// Rechecks iRacing's bindings when its controls.cfg changes or the preset's vJoy axes do (looked at once a second).
        /// </summary>
        private async void RefreshBindingCheck(bool force)
        {
            if (bindingCheckRunning || (!force && DateTime.UtcNow < nextBindingCheck)) return;
            nextBindingCheck = DateTime.UtcNow.AddSeconds(1);
            var preset = plugin.ActivePreset;
            bool clutch = plugin.Settings.ClutchEnabled;
            string key = $"{File.GetLastWriteTimeUtc(IRacingBindings.ControlsPath).Ticks}|{File.GetLastWriteTimeUtc(IRacingBindings.CalibrationPath).Ticks}|" +
                         $"{preset.Throttle.OutputAxis}|{preset.Brake.OutputAxis}|{(clutch ? preset.Clutch.OutputAxis.ToString() : "-")}";
            if (!force && key == checkedBindingsFor) return;
            checkedBindingsFor = key;

            bindingCheckRunning = true;
            try
            {
                int vendor = plugin.Settings.VendorId;
                var result = await Task.Run(() => IRacingBindings.Check(preset, clutch, vendor));
                IRacingBindingText.Text = result.Text;
                // Same amber as the unsaved-preset warning.
                IRacingBindingText.Foreground = result.Ok ? VJoyStatusText.Foreground : ModifiedText.Foreground;
            }
            catch (Exception ex)
            {
                IRacingBindingText.Text = "iRacing: could not read the bindings (" + ex.Message + ")";
                IRacingBindingText.Foreground = VJoyStatusText.Foreground;
            }
            finally
            {
                bindingCheckRunning = false;
            }
        }

        private void OnClutchToggleClick(object sender, RoutedEventArgs e)
        {
            plugin.Settings.ClutchEnabled = ClutchToggle.IsChecked == true;
            if (!plugin.Settings.ClutchEnabled && plugin.Engine.ActiveSweep == SweepPedal.Clutch) plugin.Engine.RequestSweep(SweepPedal.None);
            plugin.RequestSave();
            ShowClutch();
        }

        private void ShowClutch()
        {
            bool on = plugin.Settings.ClutchEnabled;
            ClutchPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            ClutchColumn.Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }

        private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
        {
            if (updatingPresetList || !(PresetCombo.SelectedItem is string name)) return;
            plugin.ActivatePreset(name);
        }

        private void OnReconnectClick(object sender, RoutedEventArgs e) => plugin.Engine.RequestReconnect();

        private void RefreshModified()
        {
            string active = plugin.Settings.ActivePresetName;
            bool modified = plugin.IsModified(active);
            SaveButton.IsEnabled = RevertButton.IsEnabled = modified;

            var others = plugin.Settings.Presets.Keys
                .Where(n => !string.Equals(n, active, StringComparison.OrdinalIgnoreCase) && plugin.IsModified(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            string text = modified ? "Unsaved changes. They're already in use; Save keeps them, Revert goes back to the last Save." : "";
            if (others.Count > 0)
                text += (text.Length > 0 ? " " : "") + "Also unsaved: " + string.Join(", ", others) + ".";
            if (ModifiedText.Text != text) ModifiedText.Text = text;
        }

        private void OnSaveClick(object sender, RoutedEventArgs e) => plugin.SaveActivePreset();

        private void OnRevertClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show($"Discard the changes to \"{plugin.Settings.ActivePresetName}\" since its last Save?",
                    "SIMSONN Pedals", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                plugin.RevertActivePreset();
        }

        private void OnSaveAsClick(object sender, RoutedEventArgs e)
        {
            var name = NewPresetName.Text.Trim();
            if (name.Length == 0) return;
            if (plugin.Settings.Presets.ContainsKey(name) &&
                MessageBox.Show($"Overwrite preset \"{name}\"?", "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            plugin.SaveActiveAs(name);
            NewPresetName.Clear();
        }

        private void OnSaveForGameClick(object sender, RoutedEventArgs e)
        {
            var game = plugin.CurrentGameName;
            if (string.IsNullOrEmpty(game)) return;
            if (!string.Equals(plugin.Settings.ActivePresetName, game, StringComparison.OrdinalIgnoreCase) &&
                plugin.Settings.Presets.ContainsKey(game) &&
                MessageBox.Show($"Overwrite the {game} preset with the current settings?", "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            plugin.SaveActiveAs(game);
        }

        private void OnSaveForCarClick(object sender, RoutedEventArgs e)
        {
            string name = plugin.CurrentCarModel;
            if (name == null || !ConfirmOverwrite(name)) return;
            plugin.SaveForCurrentCar();
        }

        private void OnSaveForClassClick(object sender, RoutedEventArgs e)
        {
            if (plugin.CurrentCarClass == null) return;
            if (!ConfirmOverwrite(SimsonnPedalsPlugin.ClassPresetName(plugin.CurrentCarClass))) return;
            plugin.SaveForCurrentClass();
        }

        private bool ConfirmOverwrite(string name) =>
            string.Equals(plugin.Settings.ActivePresetName, name, StringComparison.OrdinalIgnoreCase) ||
            !plugin.Settings.Presets.ContainsKey(name) ||
            MessageBox.Show($"Overwrite preset \"{name}\" with the current settings?", "SIMSONN Pedals", MessageBoxButton.YesNo) == MessageBoxResult.Yes;

        /// <summary>One line: the game and car SimHub sees, and which preset the automatic choice gives them.</summary>
        private string DescribeAutoPreset()
        {
            string game = plugin.CurrentGameName;
            if (string.IsNullOrEmpty(game)) return "No game selected in SimHub.";
            string car = plugin.CurrentCarModel == null ? "no car yet"
                : plugin.CurrentCarClass == null ? plugin.CurrentCarModel : $"{plugin.CurrentCarModel} ({plugin.CurrentCarClass})";
            string auto = plugin.ResolvePreset(game, plugin.CurrentCarId, plugin.CurrentCarClassKey, out var level);
            string why = level == SimsonnPedalsPlugin.PresetLevel.Car ? "saved for this car"
                : level == SimsonnPedalsPlugin.PresetLevel.Class ? "saved for this class"
                : level == SimsonnPedalsPlugin.PresetLevel.Game ? $"saved for {game}"
                : "nothing saved for this car, class or game";
            string text = $"{game}: {car}. Automatic choice: \"{auto}\" ({why}).";
            if (!string.Equals(auto, plugin.Settings.ActivePresetName, StringComparison.OrdinalIgnoreCase))
                text += $" In use now: \"{plugin.Settings.ActivePresetName}\", picked by hand.";
            return text;
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var name = plugin.Settings.ActivePresetName;
            if (MessageBox.Show($"Delete preset \"{name}\"?", "SIMSONN Pedals", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                plugin.DeletePreset(name);
        }

        private void OnAutoApplyClick(object sender, RoutedEventArgs e)
        {
            plugin.Settings.AutoApplyPerGame = AutoApplyToggle.IsChecked == true;
            plugin.RequestSave();
            if (plugin.Settings.AutoApplyPerGame) plugin.ApplyAutoPreset();
        }

        private async void OnReadPedalSettingsClick(object sender, RoutedEventArgs e)
        {
            if (plugin.IsGameRunning &&
                MessageBox.Show("A game is running. The pedals may stop responding for a few seconds. Continue?",
                    "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;

            ReadPedalSettingsButton.IsEnabled = false;
            PedalSettingsStatus.Text = "Reading settings from the pedals…";
            try
            {
                int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
                string label = DumpLabel.Text;
                var result = await Task.Run(() => PedalConfigReader.Read(vid, pid, label, includeLiveStatus: true));
                // Clear it so the next read is not saved under the previous change's name.
                if (result.Success) DumpLabel.Clear();
                PedalSettingsStatus.Text = result.ReportPath == null
                    ? result.Summary
                    : $"{result.Summary} Saved to {Path.GetFileName(result.ReportPath)}{(result.BinaryPath != null ? " and .bin" : "")}.";
                if (result.Success)
                {
                    PedalSettingsStatus.Text += Environment.NewLine + $"Firmware {PedalSettingsLayout.FirmwareVersionText(result.Data)}" +
                                                Environment.NewLine + PedalSettingsLayout.Describe(result.Data);
                    string axes = SyncSourceAxes(result.Data);
                    if (axes != null) PedalSettingsStatus.Text += Environment.NewLine + axes;
                    PedalSettingsStatus.Text += Environment.NewLine + (result.LiveSummary ?? "Live status: no answer (see the report file).");
                }
            }
            catch (Exception ex)
            {
                PedalSettingsStatus.Text = "Failed: " + ex.Message;
            }
            finally
            {
                ReadPedalSettingsButton.IsEnabled = true;
            }
        }

        private async void OnSetupPedalsClick(object sender, RoutedEventArgs e)
        {
            if (plugin.IsGameRunning)
            {
                MessageBox.Show("Exit the game first: the pedals restart while they save.", "SIMSONN Pedals");
                return;
            }
            if (!double.TryParse(SetupMaxForceBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double kg))
            {
                PedalSettingsStatus.Text = "Enter the brake max force in kg.";
                return;
            }

            SetPedalButtonsEnabled(false);
            PedalSettingsStatus.Text = "Reading the current settings…";
            try
            {
                int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
                var current = await Task.Run(() => PedalConfigReader.Read(vid, pid, "before setup"));
                if (!current.Success)
                {
                    PedalSettingsStatus.Text = "Nothing was written: " + current.Summary;
                    return;
                }

                byte[] wanted;
                try
                {
                    wanted = PedalSettingsLayout.BaseSetup(current.Data, kg);
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is NotSupportedException)
                {
                    // The message's second line is the parameter name.
                    PedalSettingsStatus.Text = "Nothing was written: " + ex.Message.Split(new[] { Environment.NewLine }, StringSplitOptions.None)[0];
                    return;
                }
                // The firmware's own throttle cut lives outside the settings block (report 6); setup switches it off.
                var cutoff = await Task.Run(() => PedalStatusReader.ReadBrakeCutoff(vid, pid, new System.Text.StringBuilder()));
                bool blockChanges = !wanted.SequenceEqual(current.Data);
                bool cutOn = cutoff?.Enabled == true;
                string cutoffNow = cutoff?.ToString() ?? "Firmware throttle cut: unknown (the pedals didn't answer)";

                if (!blockChanges && !cutOn)
                {
                    plugin.SetPedalMaxForce(kg);
                    PedalSettingsStatus.Text = "The pedals are already set up this way." + Environment.NewLine +
                                               PedalSettingsLayout.Describe(current.Data) + Environment.NewLine + cutoffNow +
                                               (SyncSourceAxes(current.Data) is string axesLine ? Environment.NewLine + axesLine : "");
                    return;
                }

                string question = "Now in the pedals:\n" + PedalSettingsLayout.Describe(current.Data) + "\n" + cutoffNow +
                                  "\n\nAfter setup:\n" + PedalSettingsLayout.Describe(blockChanges ? wanted : current.Data) +
                                  (cutOn ? "\nFirmware throttle cut: off (the plugin does throttle cut itself)" : "\n" + cutoffNow) + "\n\n";
                question += blockChanges
                    ? "Calibration is kept. The pedals save and restart, so they drop off USB for a few seconds. " +
                      "Close Simsonn Manager first. The current settings were saved to the dump folder. Write?"
                    : "Only the throttle cut changes; the pedals don't restart. Close Simsonn Manager first. Write?";
                if (MessageBox.Show(question, "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                {
                    PedalSettingsStatus.Text = "Nothing was written.";
                    return;
                }

                var lines = new System.Collections.Generic.List<string>();
                // Live change first, before the block write restarts the pedals; a later Read pedal settings shows whether it survived.
                if (cutOn)
                {
                    PedalSettingsStatus.Text = "Switching the firmware throttle cut off…";
                    var cut = await Task.Run(() => PedalCommandWriter.SwitchThrottleCutOff(vid, pid));
                    lines.Add(cut.ReportPath == null ? cut.Summary : $"{cut.Summary} Report: {Path.GetFileName(cut.ReportPath)}.");
                }

                bool blockWritten = !blockChanges;
                if (blockChanges)
                {
                    PedalSettingsStatus.Text = "Writing the settings to the pedals…";
                    var result = await Task.Run(() => PedalConfigWriter.Write(vid, pid, current.Data, wanted, "setup"));
                    blockWritten = result.Success;
                    lines.Insert(0, result.ReportPath == null ? result.Summary : $"{result.Summary} Report: {Path.GetFileName(result.ReportPath)}.");
                    if (result.Success) lines.Insert(1, PedalSettingsLayout.Describe(wanted));
                }

                if (blockWritten)
                {
                    plugin.SetPedalMaxForce(kg);
                    string axes = SyncSourceAxes(current.Data);
                    if (axes != null) lines.Add(axes);
                }
                PedalSettingsStatus.Text = string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                PedalSettingsStatus.Text = "Failed: " + ex.Message;
            }
            finally
            {
                SetPedalButtonsEnabled(true);
            }
        }

        /// <summary>Points the presets at the joystick axes the pedals send; a line to show if that changed anything.</summary>
        private string SyncSourceAxes(byte[] block)
        {
            if (!PedalSettingsLayout.IsLayoutVerified(block) || !plugin.SetPedalSourceAxes(block)) return null;
            return $"Throttle, brake and clutch now read joystick axes {(InputAxis)PedalSettingsLayout.ThrottleAxis(block)}, " +
                   $"{(InputAxis)PedalSettingsLayout.BrakeAxis(block)} and {(InputAxis)PedalSettingsLayout.ClutchAxis(block)} in every preset (where firmware {PedalSettingsLayout.FirmwareVersionText(block)} sends them).";
        }

        private void SetPedalButtonsEnabled(bool enabled) =>
            ReadPedalSettingsButton.IsEnabled = RewriteTestButton.IsEnabled = SetupPedalsButton.IsEnabled =
                CalibratePedalsButton.IsEnabled = LiveWriteTestButton.IsEnabled = MaxForceTestButton.IsEnabled =
                    BrakeCutTestButton.IsEnabled = enabled;

        // Calibration of the pedals themselves: live raw values from the params report, rest → press → Finish.
        private PedalRawMonitor rawMonitor;
        private System.Windows.Threading.DispatcherTimer calibrationTimer;
        private bool calibrationCapturing;
        private int calibrationAxisBase; // raw channel of the throttle; the brake is the next one

        private async void OnCalibratePedalsClick(object sender, RoutedEventArgs e)
        {
            if (plugin.IsGameRunning)
            {
                MessageBox.Show("Exit the game first: the pedals restart when the calibration is saved.", "SIMSONN Pedals");
                return;
            }
            // Which axes carry the pedals depends on the firmware's settings (0–2 on 2.0.x, 3–5 on 2.1.x).
            SetPedalButtonsEnabled(false);
            PedalSettingsStatus.Text = "Reading the current settings…";
            int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
            var current = await Task.Run(() => PedalConfigReader.Read(vid, pid, "before calibration start"));
            SetPedalButtonsEnabled(true);
            if (!current.Success)
            {
                PedalSettingsStatus.Text = "Can't calibrate: " + current.Summary;
                return;
            }
            if (!PedalSettingsLayout.IsLayoutVerified(current.Data))
            {
                PedalSettingsStatus.Text = $"Can't calibrate: the plugin doesn't know the settings layout of firmware {PedalSettingsLayout.FirmwareVersionText(current.Data)}.";
                return;
            }
            calibrationAxisBase = PedalSettingsLayout.PedalAxisBase(current.Data);
            rawMonitor = PedalRawMonitor.Open(vid, pid, out string error);
            if (rawMonitor == null)
            {
                PedalSettingsStatus.Text = "Can't calibrate: " + error;
                return;
            }
            calibrationCapturing = false;
            SetPedalButtonsEnabled(false);
            ShowCalibrationButtons(true);
            CalibrationStartButton.IsEnabled = true;
            CalibrationFinishButton.IsEnabled = false;
            PedalSettingsStatus.Text = "Close Simsonn Manager. Take your feet off both pedals, then click Start.";
            calibrationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            calibrationTimer.Tick += (s, a) => ShowCalibrationLive();
            calibrationTimer.Start();
        }

        private void OnCalibrationStartClick(object sender, RoutedEventArgs e)
        {
            rawMonitor?.ResetRange();
            calibrationCapturing = true;
            CalibrationStartButton.IsEnabled = false;
            CalibrationFinishButton.IsEnabled = true;
            PedalSettingsStatus.Text = "Press the throttle all the way a few times and the brake firmly, let both go, then click Finish.";
        }

        private void ShowCalibrationLive()
        {
            if (rawMonitor == null) return;
            string error = rawMonitor.Error;
            if (error != null)
            {
                StopCalibration();
                PedalSettingsStatus.Text = $"Calibration stopped: lost the pedals ({error}). Nothing was written.";
                return;
            }
            var now = rawMonitor.Latest;
            int t = calibrationAxisBase, b = calibrationAxisBase + 1;
            string text = $"Throttle {now[t],6}   Brake {now[b],6}";
            if (calibrationCapturing && rawMonitor.Samples > 0)
            {
                int[] min = rawMonitor.Min, max = rawMonitor.Max;
                text += $"   |   throttle {min[t]}…{max[t]}, brake {min[b]}…{max[b]}";
            }
            CalibrationLive.Text = text;
        }

        private void OnCalibrationCancelClick(object sender, RoutedEventArgs e)
        {
            StopCalibration();
            PedalSettingsStatus.Text = "Calibration cancelled; nothing was written.";
        }

        private void StopCalibration()
        {
            calibrationTimer?.Stop();
            calibrationTimer = null;
            rawMonitor?.Dispose();
            rawMonitor = null;
            calibrationCapturing = false;
            ShowCalibrationButtons(false);
            CalibrationLive.Text = "";
            SetPedalButtonsEnabled(true);
        }

        private void ShowCalibrationButtons(bool calibrating)
        {
            var shown = calibrating ? Visibility.Visible : Visibility.Collapsed;
            CalibrationStartButton.Visibility = CalibrationFinishButton.Visibility = CalibrationCancelButton.Visibility = shown;
            CalibratePedalsButton.Visibility = calibrating ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void OnCalibrationFinishClick(object sender, RoutedEventArgs e)
        {
            if (rawMonitor == null) return;
            int samples = rawMonitor.Samples;
            int[] min = rawMonitor.Min, max = rawMonitor.Max;
            StopCalibration();
            if (samples == 0)
            {
                PedalSettingsStatus.Text = "No sensor values arrived from the pedals; nothing was written.";
                return;
            }
            if (!double.TryParse(SetupMaxForceBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double kg))
            {
                PedalSettingsStatus.Text = "Enter the brake max force in kg; nothing was written.";
                return;
            }

            SetPedalButtonsEnabled(false);
            PedalSettingsStatus.Text = "Reading the current settings…";
            try
            {
                int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
                var current = await Task.Run(() => PedalConfigReader.Read(vid, pid, "before calibration"));
                if (!current.Success)
                {
                    PedalSettingsStatus.Text = "Nothing was written: " + current.Summary;
                    return;
                }

                byte[] wanted;
                try
                {
                    int t = calibrationAxisBase, b = calibrationAxisBase + 1;
                    if (PedalSettingsLayout.PedalAxisBase(current.Data) != calibrationAxisBase)
                        throw new NotSupportedException("The pedals' axis setup changed during calibration. Start again.");
                    wanted = PedalSettingsLayout.Calibrate(current.Data, min[t], max[t], min[b], kg);
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is NotSupportedException)
                {
                    // The message's second line is the parameter name.
                    PedalSettingsStatus.Text = "Nothing was written: " + ex.Message.Split(new[] { Environment.NewLine }, StringSplitOptions.None)[0];
                    return;
                }

                if (MessageBox.Show(
                        "Now in the pedals:\n" + PedalSettingsLayout.DescribeCalibration(current.Data) + "\n" + PedalSettingsLayout.Describe(current.Data) +
                        "\n\nAfter calibration:\n" + PedalSettingsLayout.DescribeCalibration(wanted) + "\n" + PedalSettingsLayout.Describe(wanted) +
                        "\n\nThe plugin's own calibration in every preset goes back to the full range, since the pedals then report " +
                        "0–100 % themselves. The pedals save and restart, so they drop off USB for a few seconds. " +
                        "The current settings were saved to the dump folder. Write?",
                        "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                {
                    PedalSettingsStatus.Text = "Nothing was written.";
                    return;
                }

                PedalSettingsStatus.Text = "Writing the calibration to the pedals…";
                var result = await Task.Run(() => PedalConfigWriter.Write(vid, pid, current.Data, wanted, "calibration"));
                if (result.Success)
                {
                    plugin.SetPedalMaxForce(kg);
                    plugin.ResetPluginCalibration();
                }
                PedalSettingsStatus.Text = result.ReportPath == null ? result.Summary : $"{result.Summary} Report: {Path.GetFileName(result.ReportPath)}.";
                if (result.Success)
                    PedalSettingsStatus.Text += Environment.NewLine + PedalSettingsLayout.DescribeCalibration(wanted) +
                                                Environment.NewLine + PedalSettingsLayout.Describe(wanted) +
                                                (SyncSourceAxes(wanted) is string axesLine ? Environment.NewLine + axesLine : "");
            }
            catch (Exception ex)
            {
                PedalSettingsStatus.Text = "Failed: " + ex.Message;
            }
            finally
            {
                SetPedalButtonsEnabled(true);
            }
        }

        private async void OnRewriteTestClick(object sender, RoutedEventArgs e)
        {
            if (plugin.IsGameRunning)
            {
                MessageBox.Show("Exit the game first: the pedals restart during the test.", "SIMSONN Pedals");
                return;
            }
            if (MessageBox.Show(
                    "This reads the settings stored in the pedals and writes exactly the same bytes back, then reads them again to check.\n\n" +
                    "The pedals save and restart, so they drop off USB for a few seconds. Close Simsonn Manager first.\n\n" +
                    "Both reads are saved in the dump folder, so the settings can be restored from them. Continue?",
                    "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;

            SetPedalButtonsEnabled(false);
            PedalSettingsStatus.Text = "Rewrite test: reading, writing and reading back…";
            try
            {
                int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
                var result = await Task.Run(() => PedalConfigWriter.RewriteTest(vid, pid));
                PedalSettingsStatus.Text = result.ReportPath == null
                    ? result.Summary
                    : $"{result.Summary} Report: {Path.GetFileName(result.ReportPath)}.";
            }
            catch (Exception ex)
            {
                PedalSettingsStatus.Text = "Failed: " + ex.Message;
            }
            finally
            {
                SetPedalButtonsEnabled(true);
            }
        }

        private void OnLiveWriteTestClick(object sender, RoutedEventArgs e) => RunDirectTest(
            "Live write test: hold the throttle about halfway for the whole test (about 15 s). The pedals get settings where the throttle reaches 100 % at half travel, then their own settings back.",
            (engine, vid, pid, progress) => DirectModeTests.LiveWrite(engine, vid, pid, progress));

        private void OnMaxForceTestClick(object sender, RoutedEventArgs e) => RunDirectTest(
            "Live max force test: press the brake lightly (about 20–40 %) and hold it steady for about 10 s. The brake's max force is halved for a few seconds, then put back.",
            (engine, vid, pid, progress) => DirectModeTests.LiveMaxForce(engine, vid, pid, progress));

        private void OnBrakeCutTestClick(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(BrakeCutTestBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double percent) &&
                !double.TryParse(BrakeCutTestBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out percent) ||
                percent < 1 || percent > PedalCommandWriter.MaxBrakeCutoffTenths / 10.0)
            {
                DirectTestStatus.Text = "Enter a brake level from 1 to 50 %.";
                return;
            }
            int tenths = (int)Math.Round(percent * 10);
            RunDirectTest(
                $"Firmware brake cut test: the pedals' own throttle cut is switched on at {percent:0.#} % brake for 10 s. Hold the throttle down and press the brake past that level and back a few times. Afterwards it's set back as it was.",
                (engine, vid, pid, progress) => DirectModeTests.FirmwareBrakeCut(engine, vid, pid, tenths, progress));
        }

        private async void RunDirectTest(string intro, Func<PedalEngine, int, int, Action<string>, DirectModeTestResult> test)
        {
            if (!plugin.Engine.DeviceConnected)
            {
                DirectTestStatus.Text = "The pedals aren't connected.";
                return;
            }
            if (MessageBox.Show(intro + "\n\nClose Simsonn Manager first. Start?", "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;

            SetPedalButtonsEnabled(false);
            void Progress(string text) => Dispatcher.BeginInvoke(new Action(() => DirectTestStatus.Text = text));
            Progress("Starting…");
            try
            {
                var engine = plugin.Engine;
                int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
                var result = await Task.Run(() => test(engine, vid, pid, Progress));
                DirectTestStatus.Text = result.ReportPath == null ? result.Summary : $"{result.Summary} Report: {Path.GetFileName(result.ReportPath)}.";
            }
            catch (Exception ex)
            {
                DirectTestStatus.Text = "Failed: " + ex.Message + " Check the settings with Read pedal settings.";
            }
            finally
            {
                SetPedalButtonsEnabled(true);
            }
        }

        private void OnOpenDumpFolderClick(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(PedalConfigReader.OutputFolder);
            Process.Start("explorer.exe", PedalConfigReader.OutputFolder);
        }

        // Pedal firmware: Check reads the version and lists the Manager's files; Update flashes the chosen one.
        private bool firmwareBusy;

        private string ManagerFolder =>
            FirmwareFile.IsManagerFolder(plugin.Settings.ManagerFolder) ? plugin.Settings.ManagerFolder : FirmwareFile.FindManagerFolder();

        private async void OnCheckFirmwareClick(object sender, RoutedEventArgs e) => await CheckFirmware();

        private async Task CheckFirmware()
        {
            if (firmwareBusy) return;
            SetFirmwareBusy(true);
            FirmwareStatus.Text = "Reading the firmware version and Simsonn Manager's firmware files…";
            FirmwareCombo.ItemsSource = null;
            try
            {
                int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
                string folder = await Task.Run(() => ManagerFolder);
                var log = new System.Text.StringBuilder();
                var files = folder == null ? new System.Collections.Generic.List<FirmwareFile>() : await Task.Run(() => FirmwareFile.FindFor(pid, folder, log));
                string versionError = null;
                bool updateMode = await Task.Run(() => PedalFirmwareUpdater.IsInUpdateMode(vid));
                var versions = updateMode ? null : await Task.Run(() => PedalRawMonitor.ReadVersions(vid, pid, 3000, out versionError));

                var lines = new System.Collections.Generic.List<string>
                {
                    updateMode
                        ? "The pedals are in update mode (SIMSONN Flasher). Close Simsonn Manager, choose a firmware and click Update firmware to finish."
                        : versions == null
                        ? "Pedals: couldn't read the firmware version (" + versionError + ")"
                        : $"Pedals run firmware {PedalRawMonitor.VersionText(versions.Value.Main, false)}, second chip {PedalRawMonitor.VersionText(versions.Value.Sub, true)}."
                };
                if (folder == null)
                    lines.Add("Simsonn Manager wasn't found. Use Find Simsonn Manager… to point at SIMSONN Manager.exe.");
                else if (files.Count == 0)
                    lines.Add($"No firmware files for these pedals in {folder}. {log}".Trim());
                else
                {
                    lines.Add($"{files.Count} firmware file(s) from {folder}, all decrypted and checksum-checked. " +
                              $"The first one is what Simsonn Manager installs; the others are older versions to go back to.");
                    if (log.Length > 0) lines.Add(log.ToString().Trim());
                }
                FirmwareCombo.ItemsSource = files;
                FirmwareCombo.SelectedIndex = files.Count > 0 ? 0 : -1;
                FirmwareStatus.Text = string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                FirmwareStatus.Text = "Failed: " + ex.Message;
            }
            finally
            {
                SetFirmwareBusy(false);
            }
        }

        private async void OnChooseManagerClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Find SIMSONN Manager.exe",
                Filter = "SIMSONN Manager|SIMSONN Manager.exe|Programs|*.exe",
            };
            if (dialog.ShowDialog() != true) return;
            // The exe lives in <install>\bin; the firmware files in <install>\env.
            string folder = Path.GetDirectoryName(Path.GetDirectoryName(dialog.FileName));
            if (!FirmwareFile.IsManagerFolder(folder))
            {
                FirmwareStatus.Text = $"{folder} doesn't look like a Simsonn Manager install (no bin\\SSNetwork.dll or env\\firmware).";
                return;
            }
            plugin.Settings.ManagerFolder = folder;
            plugin.Save();
            await CheckFirmware();
        }

        private async void OnUpdateFirmwareClick(object sender, RoutedEventArgs e)
        {
            if (!(FirmwareCombo.SelectedItem is FirmwareFile firmware) || firmwareBusy) return;
            if (plugin.IsGameRunning)
            {
                MessageBox.Show("Exit the game first.", "SIMSONN Pedals");
                return;
            }
            if (MessageBox.Show(
                    $"Update the pedals to firmware {firmware.MainVersion} (second chip {firmware.SubVersion}) from {firmware.FileName}?\n\n" +
                    "• Close Simsonn Manager first.\n" +
                    "• The pedals restart into update mode and drop off USB; don't unplug them until this finishes (about a minute).\n" +
                    "• Pedal settings, calibration and max force may be reset by the new firmware; check them afterwards.\n" +
                    "• On firmware 2.1.x, Set up pedals and Calibrate pedals stay off until its settings layout has been checked.\n" +
                    "• If it stops part way, the pedals stay in update mode: run the update again, here or in Simsonn Manager.",
                    "SIMSONN Pedals – firmware update", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            SetFirmwareBusy(true);
            SetPedalButtonsEnabled(false);
            try
            {
                int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
                var result = await Task.Run(() => PedalFirmwareUpdater.Update(vid, pid, firmware,
                    line => Dispatcher.BeginInvoke(new Action(() => FirmwareStatus.Text = line))));
                FirmwareStatus.Text = result.ReportPath == null ? result.Summary : $"{result.Summary}{Environment.NewLine}Report: {Path.GetFileName(result.ReportPath)}.";
                if (result.Success)
                    FirmwareStatus.Text += Environment.NewLine + "Use Read pedal settings to see what the new firmware kept.";
                MessageBox.Show(result.Summary, "SIMSONN Pedals – firmware update", MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                FirmwareStatus.Text = "Failed: " + ex.Message;
            }
            finally
            {
                SetPedalButtonsEnabled(true);
                SetFirmwareBusy(false);
            }
        }

        private void SetFirmwareBusy(bool busy)
        {
            firmwareBusy = busy;
            CheckFirmwareButton.IsEnabled = ChooseManagerButton.IsEnabled = FirmwareCombo.IsEnabled = !busy;
            UpdateFirmwareButton.IsEnabled = !busy && FirmwareCombo.SelectedItem != null;
        }

        private void ShowUpdateState(string message = null)
        {
            var current = PluginUpdater.CurrentVersion;
            var latest = plugin.LatestRelease;
            bool available = latest != null && latest.Version > current && latest.DownloadUrl != null && plugin.PendingUpdateVersion == null;

            InstallUpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            InstallUpdateButton.Content = available ? $"Update to {latest.Version}" : "Update";
            RestartButton.Visibility = plugin.PendingUpdateVersion != null ? Visibility.Visible : Visibility.Collapsed;

            if (message == null)
            {
                if (plugin.PendingUpdateVersion != null)
                    message = $"Version {plugin.PendingUpdateVersion} is installed and loads when SimHub restarts. You'll be asked to approve the changed plugin.";
                else if (available)
                    message = $"Version {latest.Version} is available (you have {current}).";
                else if (latest != null)
                    message = $"Version {current} is the latest.";
                else
                    message = $"Version {current}.";
            }
            UpdateStatusText.Text = message;
        }

        private async void CheckForUpdates(bool quiet)
        {
            CheckUpdatesButton.IsEnabled = false;
            if (!quiet) ShowUpdateState("Checking GitHub for updates…");
            try
            {
                plugin.LatestRelease = await Task.Run(PluginUpdater.GetLatestRelease);
                ShowUpdateState(plugin.LatestRelease == null ? $"Version {PluginUpdater.CurrentVersion}. No releases published yet." : null);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[SimsonnPedals] Update check failed: {ex.Message}");
                // A failed background check shouldn't look like an error on every page open.
                ShowUpdateState(quiet ? null : "Couldn't check for updates: " + ex.Message);
            }
            finally
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }

        private void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => CheckForUpdates(quiet: false);

        private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
        {
            var release = plugin.LatestRelease;
            if (release == null) return;
            if (plugin.IsGameRunning &&
                MessageBox.Show("A game is running. The update only takes effect after SimHub restarts. Download it now?",
                    "SIMSONN Pedals", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;

            InstallUpdateButton.IsEnabled = CheckUpdatesButton.IsEnabled = false;
            ShowUpdateState($"Downloading version {release.Version}…");
            try
            {
                await Task.Run(() => PluginUpdater.Install(release));
                plugin.PendingUpdateVersion = release.Version;
                ShowUpdateState();
                if (!plugin.IsGameRunning &&
                    MessageBox.Show($"Version {release.Version} is installed. Restart SimHub now to load it?",
                        "SIMSONN Pedals", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    Restart();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[SimsonnPedals] Update failed", ex);
                ShowUpdateState("Update failed: " + ex.Message);
            }
            finally
            {
                InstallUpdateButton.IsEnabled = CheckUpdatesButton.IsEnabled = true;
            }
        }

        private void OnRestartClick(object sender, RoutedEventArgs e) => Restart();

        private void Restart()
        {
            if (!PluginUpdater.TryRestartSimHub())
                ShowUpdateState("Couldn't restart SimHub automatically. Exit it from the tray icon and start it again to finish the update.");
        }

        private void OnReleaseNotesClick(object sender, RoutedEventArgs e)
        {
            var url = plugin.LatestRelease?.PageUrl ?? PluginUpdater.RepositoryUrl + "/releases";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private void OnVJoyDeviceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VJoyDeviceCombo.SelectedItem is uint id && id != plugin.Settings.VJoyDeviceId)
            {
                plugin.Settings.VJoyDeviceId = id;
                plugin.RequestSave();
            }
        }
    }
}
