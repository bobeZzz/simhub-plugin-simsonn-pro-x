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

            RefreshModified();
            BrakePanel.Refresh(engine.DeviceConnected);
            ThrottlePanel.Refresh(engine.DeviceConnected);
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
            if (plugin.Settings.AutoApplyPerGame) plugin.ApplyPresetForGame(plugin.CurrentGameName);
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
                                               PedalSettingsLayout.Describe(current.Data) + Environment.NewLine + cutoffNow;
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

                if (blockWritten) plugin.SetPedalMaxForce(kg);
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

        private void SetPedalButtonsEnabled(bool enabled) =>
            ReadPedalSettingsButton.IsEnabled = RewriteTestButton.IsEnabled = SetupPedalsButton.IsEnabled =
                CalibratePedalsButton.IsEnabled = enabled;

        // Calibration of the pedals themselves: live raw values from the params report, rest → press → Finish.
        private PedalRawMonitor rawMonitor;
        private System.Windows.Threading.DispatcherTimer calibrationTimer;
        private bool calibrationCapturing;

        private void OnCalibratePedalsClick(object sender, RoutedEventArgs e)
        {
            if (plugin.IsGameRunning)
            {
                MessageBox.Show("Exit the game first: the pedals restart when the calibration is saved.", "SIMSONN Pedals");
                return;
            }
            rawMonitor = PedalRawMonitor.Open(plugin.Settings.VendorId, plugin.Settings.ProductId, out string error);
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
            string text = $"Throttle {now[0],6}   Brake {now[1],6}";
            if (calibrationCapturing && rawMonitor.Samples > 0)
            {
                int[] min = rawMonitor.Min, max = rawMonitor.Max;
                text += $"   |   throttle {min[0]}…{max[0]}, brake {min[1]}…{max[1]}";
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
                    wanted = PedalSettingsLayout.Calibrate(current.Data, min[0], max[0], min[1], kg);
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
                                                Environment.NewLine + PedalSettingsLayout.Describe(wanted);
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
                var versions = await Task.Run(() => PedalRawMonitor.ReadVersions(vid, pid, 3000, out versionError));

                var lines = new System.Collections.Generic.List<string>
                {
                    versions == null
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
