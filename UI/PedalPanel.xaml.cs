using System;
using System.Windows;
using System.Windows.Controls;

namespace SimsonnPedals.UI
{
    public partial class PedalPanel : UserControl
    {
        private PedalConfig config;

        public PedalPanel()
        {
            InitializeComponent();
            SourceAxisCombo.ItemsSource = Enum.GetValues(typeof(InputAxis));
            OutputAxisCombo.ItemsSource = Enum.GetValues(typeof(VJoyAxis));
        }

        public string Title
        {
            get => TitleText.Text;
            set => TitleText.Text = value;
        }

        public PedalProcessor Processor { get; set; }

        /// <summary>
        /// Direct mode: the pedals shape the axis, so the bars show their output and the plugin's own calibration (which
        /// works on the unshaped axis) is off; Calibrate pedals does it in the pedals.
        /// </summary>
        public bool DirectMode { get; set; }

        public PedalEngine Engine { get; set; }

        /// <summary>Which pedal this panel's binding sweep drives.</summary>
        public SweepPedal SweepKind { get; set; }

        /// <summary>Shows the max force (kg) controls; only meaningful for the load-cell brake.</summary>
        public bool ShowMaxForce
        {
            get => MaxForceSection.Visibility == Visibility.Visible;
            set => MaxForceSection.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public PedalConfig Config
        {
            get => config;
            set
            {
                if (Processor != null && Processor.IsCalibrating) Processor.CancelCalibration();
                config = value;
                DataContext = value;
                Curve.Config = value;
            }
        }

        /// <summary>Refreshes the live readouts; called by the parent's UI timer.</summary>
        public void Refresh(bool connected)
        {
            if (Processor == null || config == null) return;

            if (!connected)
            {
                PositionBar.Value = 0;
                OutputBar.Value = 0;
                PositionText.Text = "—";
                OutputText.Text = "—";
                Curve.SetLive(double.NaN, double.NaN);
            }
            else
            {
                PositionBar.Value = Processor.Position;
                OutputBar.Value = Processor.Output;
                PositionText.Text = DirectMode
                    ? $"{Processor.Position * 100:0.0}% (pedals' output)"
                    : ShowMaxForce && config.HasForceScaling
                        ? $"{Processor.Position * 100:0.0}% | {Processor.Position * config.FirmwareMaxForceKg:0.0} kg"
                        : $"{Processor.Position * 100:0.0}%  ({Processor.Raw})";
                OutputText.Text = $"{Processor.Output * 100:0.0}%";
                Curve.SetLive(Processor.CurveInput, Processor.Output);
            }

            if (Engine != null)
            {
                bool mine = Engine.ActiveSweep == SweepKind && SweepKind != SweepPedal.None;
                SweepButton.Content = mine ? "Stop" : "Move vJoy axis";
                SweepButton.IsEnabled = connected && Engine.VJoyReady;
                SweepText.Text = !Engine.VJoyReady
                    ? "Needs vJoy (see the vJoy line at the top of the page)."
                    : mine && Engine.SweepCountdownSeconds > 0
                        ? $"Starting in {Math.Ceiling(Engine.SweepCountdownSeconds)} s; switch to the game now and don't touch the pedals."
                        : mine
                            ? "Moving the vJoy axis 0 → 100% → 0, twice."
                            : "Start the game's pedal calibration, click here, switch back and don't touch the pedals.";
            }

            // The binding sweep moves a vJoy axis; in direct mode the game binds to the pedals themselves.
            var bindVisibility = DirectMode ? Visibility.Collapsed : Visibility.Visible;
            if (BindRow.Visibility != bindVisibility)
                BindCaption.Visibility = BindRow.Visibility = OutputAxisLabel.Visibility = OutputAxisCombo.Visibility = bindVisibility;
            CalibrateButton.IsEnabled = !DirectMode || Processor.IsCalibrating;
            if (DirectMode && !Processor.IsCalibrating)
            {
                CalibrateButton.Content = "Calibrate";
                CalibrationText.Text = "In direct mode use Calibrate pedals on the Pedal setup tab.";
            }
            else if (Processor.IsCalibrating)
            {
                CalibrateButton.Content = "Finish";
                SetupExpander.IsExpanded = true;
                CalibrationText.Text = $"Press the pedal fully a few times. Seen {Processor.CalibrationSeenMin} – {Processor.CalibrationSeenMax}";
            }
            else
            {
                CalibrateButton.Content = "Calibrate";
                CalibrationText.Text = $"Range {config.CalibrationMin} – {config.CalibrationMax}";
            }
        }

        private void OnShapeClick(object sender, RoutedEventArgs e)
        {
            if (config == null) return;
            if (Enum.TryParse((string)((Button)sender).Tag, out CurveShape shape)) config.ApplyShape(shape);
        }

        private void OnCalibrateClick(object sender, RoutedEventArgs e)
        {
            if (Processor == null || config == null) return;
            if (!Processor.IsCalibrating)
            {
                Processor.StartCalibration();
                return;
            }
            if (!Processor.FinishCalibration(config))
                MessageBox.Show("The pedal barely moved, so the calibration was not changed.", "SIMSONN Pedals");
        }

        private void OnSweepClick(object sender, RoutedEventArgs e)
        {
            if (Engine == null) return;
            Engine.RequestSweep(Engine.ActiveSweep == SweepKind ? SweepPedal.None : SweepKind);
        }

        private void OnResetCalibrationClick(object sender, RoutedEventArgs e)
        {
            Processor?.CancelCalibration();
            config?.ResetCalibration();
        }
    }
}
