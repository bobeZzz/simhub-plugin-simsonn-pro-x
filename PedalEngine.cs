using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SharpDX;
using SharpDX.DirectInput;

namespace SimsonnPedals
{
    /// <summary>
    /// Background loop: reads the physical pedals through DirectInput, shapes them and feeds vJoy.
    /// Runs on its own thread because SimHub's DataUpdate only ticks at ~60 Hz.
    /// </summary>
    public enum SweepPedal { None, Throttle, Brake, Clutch }

    public sealed class PedalEngine : IDisposable
    {
        private const double ReconnectIntervalMs = 1000;
        // After an unplug, give Windows and the pedals' firmware time to finish enumerating before opening.
        private const double ReplugSettleMs = 1500;
        private const double AttachedCheckIntervalMs = 1000;
        private const int ReportWaitMs = 4;

        // Binding sweep: a countdown to get back to the game's calibration wizard, then two full 0→100→0 strokes.
        private const double SweepCountdownMs = 5000;
        private const double SweepStrokeMs = 2000;
        private const int SweepStrokes = 2;

        private volatile int sweepRequest;
        private SweepPedal sweepActive;
        private double sweepStartMs;

        private readonly Func<PluginSettings> settings;
        private readonly AutoResetEvent reportSignal = new AutoResetEvent(false);
        private Thread thread;
        private volatile bool stopping;
        private volatile bool reconnectRequest;
        private volatile PedalPreset activePreset;

        public PedalEngine(Func<PluginSettings> settings)
        {
            this.settings = settings;
        }

        public PedalProcessor Throttle { get; } = new PedalProcessor();
        public PedalProcessor Brake { get; } = new PedalProcessor();
        public PedalProcessor Clutch { get; } = new PedalProcessor();

        public PedalPreset ActivePreset
        {
            get => activePreset;
            set => activePreset = value;
        }

        public bool DeviceConnected { get; private set; }
        public string DeviceName { get; private set; } = "";
        public string VJoyStatus { get; private set; } = "Not started";
        public bool VJoyReady { get; private set; }

        /// <summary>Every axis exactly as DirectInput reports it, for diagnosing mapping problems.</summary>
        public string RawAxes { get; private set; } = "";

        /// <summary>Approximate input updates per second, for diagnostics.</summary>
        public double UpdateRate { get; private set; }

        /// <summary>Pedal whose vJoy axis is currently driven by a binding sweep instead of the real pedal.</summary>
        public SweepPedal ActiveSweep => sweepActive;

        /// <summary>Seconds left before the sweep starts moving, 0 once it is moving.</summary>
        public double SweepCountdownSeconds { get; private set; }

        /// <summary>
        /// Moves only the vJoy axis of <paramref name="pedal"/> through its full range, so a game's calibration
        /// wizard binds vJoy rather than the physical pedal (which stays still). Pass None to cancel.
        /// </summary>
        public void RequestSweep(SweepPedal pedal) => sweepRequest = pedal == SweepPedal.None ? -1 : (int)pedal;

        /// <summary>Closes and reopens the pedals on the input thread, as if they had been replugged.</summary>
        public void RequestReconnect() => reconnectRequest = true;

        public void Start()
        {
            if (thread != null) return;
            stopping = false;
            thread = new Thread(Run) { IsBackground = true, Name = "SimsonnPedals input", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void Dispose()
        {
            // May run from both End() and ProcessExit; only the first caller stops the thread.
            var running = Interlocked.Exchange(ref thread, null);
            if (running == null) return;
            stopping = true;
            reportSignal.Set();
            running.Join(2000);
        }

        private void Run()
        {
            var clock = Stopwatch.StartNew();
            Joystick joystick = null;
            Guid instanceGuid = Guid.Empty;
            DirectInput directInput = null;
            double nextConnectMs = 0;
            double nextAttachedCheckMs = 0;
            double seenSinceMs = -1;
            bool replugged = false;
            double rateWindowStart = 0;
            int rateCount = 0;

            using (var vjoy = new VJoyOutput())
            {
                // vJoy holds its last values, so release the pedals in the game as soon as input stops.
                void Drop(string reason)
                {
                    vjoy.ZeroAll();
                    SafeDispose(joystick);
                    joystick = null;
                    DeviceConnected = false;
                    RawAxes = "";
                    replugged = true;
                    seenSinceMs = -1;
                    SimHub.Logging.Current.Info($"[SimsonnPedals] Pedals disconnected ({reason})");
                }

                while (!stopping)
                {
                    try
                    {
                        var s = settings();
                        double now = clock.Elapsed.TotalMilliseconds;

                        vjoy.Ensure(s.VJoyDeviceId, now);
                        VJoyStatus = vjoy.Status;
                        bool ready = vjoy.IsReady;
                        if (ready && !VJoyReady)
                        {
                            // Freshly acquired: clear whatever an earlier session or feeder left on these axes.
                            var current = activePreset;
                            if (current != null)
                            {
                                vjoy.SetAxis(current.Throttle.OutputAxis, 0);
                                vjoy.SetAxis(current.Brake.OutputAxis, 0);
                                if (s.ClutchEnabled) vjoy.SetAxis(current.Clutch.OutputAxis, 0);
                            }
                        }
                        VJoyReady = ready;

                        if (reconnectRequest)
                        {
                            reconnectRequest = false;
                            if (joystick != null) Drop("reconnect requested");
                            nextConnectMs = 0;
                        }

                        if (joystick != null && now >= nextAttachedCheckMs)
                        {
                            // Some unplugs never make Poll fail, so also ask DirectInput whether the device is still there.
                            nextAttachedCheckMs = now + AttachedCheckIntervalMs;
                            if (!directInput.IsDeviceAttached(instanceGuid)) Drop("no longer attached");
                        }

                        if (joystick == null)
                        {
                            DeviceConnected = false;
                            if (now >= nextConnectMs)
                            {
                                nextConnectMs = now + ReconnectIntervalMs;
                                // A fresh DirectInput session per attempt: one that saw the device vanish can keep
                                // handing out a joystick that acquires fine but only reports zeros.
                                directInput?.Dispose();
                                directInput = new DirectInput();
                                var device = FindDevice(directInput, s.VendorId, s.ProductId);
                                if (device == null)
                                {
                                    seenSinceMs = -1;
                                }
                                else
                                {
                                    if (seenSinceMs < 0) seenSinceMs = now;
                                    if (!replugged || now - seenSinceMs >= ReplugSettleMs)
                                    {
                                        joystick = TryOpen(directInput, device);
                                        if (joystick != null)
                                        {
                                            instanceGuid = device.InstanceGuid;
                                            nextAttachedCheckMs = now + AttachedCheckIntervalMs;
                                            replugged = false;
                                        }
                                    }
                                }
                            }
                            if (joystick == null)
                            {
                                Thread.Sleep(50);
                                continue;
                            }
                            DeviceConnected = true;
                        }

                        reportSignal.WaitOne(ReportWaitMs);
                        JoystickState state;
                        try
                        {
                            joystick.Poll();
                            state = joystick.GetCurrentState();
                        }
                        catch (Exception ex)
                        {
                            // Unplugged or lost: drop it and let the reconnect path pick it up again.
                            Drop(ex.GetType().Name + ": " + ex.Message);
                            continue;
                        }

                        RawAxes = $"X {state.X}  Y {state.Y}  Z {state.Z}  Rx {state.RotationX}  Ry {state.RotationY}  Rz {state.RotationZ}  S0 {state.Sliders[0]}  S1 {state.Sliders[1]}";

                        now = clock.Elapsed.TotalMilliseconds;

                        var preset = activePreset;
                        if (preset == null) continue;

                        double throttle = Throttle.Process(preset.Throttle, ReadAxis(state, preset.Throttle.SourceAxis));
                        double brake = Brake.Process(preset.Brake, ReadAxis(state, preset.Brake.SourceAxis));
                        bool clutchOn = s.ClutchEnabled;
                        double clutch = clutchOn ? Clutch.Process(preset.Clutch, ReadAxis(state, preset.Clutch.SourceAxis)) : 0;

                        int request = sweepRequest;
                        if (request != 0)
                        {
                            sweepRequest = 0;
                            sweepActive = request < 0 ? SweepPedal.None : (SweepPedal)request;
                            sweepStartMs = now;
                        }
                        if (sweepActive != SweepPedal.None)
                        {
                            double sweep = SweepValue(now - sweepStartMs);
                            if (double.IsNaN(sweep)) sweepActive = SweepPedal.None;
                            else if (sweepActive == SweepPedal.Throttle) throttle = sweep;
                            else if (sweepActive == SweepPedal.Brake) brake = sweep;
                            else if (clutchOn) clutch = sweep;
                        }

                        // A switched-off clutch leaves its vJoy axis to ZeroUnused, which parks it at 0.
                        vjoy.ZeroUnused(preset.Throttle.OutputAxis, preset.Brake.OutputAxis, clutchOn ? preset.Clutch.OutputAxis : preset.Throttle.OutputAxis);
                        vjoy.SetAxis(preset.Throttle.OutputAxis, throttle);
                        vjoy.SetAxis(preset.Brake.OutputAxis, brake);
                        if (clutchOn) vjoy.SetAxis(preset.Clutch.OutputAxis, clutch);

                        rateCount++;
                        if (now - rateWindowStart >= 1000)
                        {
                            UpdateRate = rateCount * 1000.0 / (now - rateWindowStart);
                            rateWindowStart = now;
                            rateCount = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Error("[SimsonnPedals] Input loop error", ex);
                        try { vjoy.ZeroAll(); }
                        catch (Exception) { /* vJoy itself may be what failed */ }
                        Thread.Sleep(500);
                    }
                }

                SafeDispose(joystick);
                directInput?.Dispose();
                DeviceConnected = false;
            }
        }

        private static DeviceInstance FindDevice(DirectInput directInput, int vendorId, int productId)
        {
            // DirectInput product GUIDs encode the USB IDs as PIDVID in the first 4 bytes.
            return directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
                .FirstOrDefault(d =>
                {
                    uint data1 = BitConverter.ToUInt32(d.ProductGuid.ToByteArray(), 0);
                    return (data1 & 0xFFFF) == vendorId && (data1 >> 16) == productId;
                });
        }

        private Joystick TryOpen(DirectInput directInput, DeviceInstance device)
        {
            var joystick = new Joystick(directInput, device.InstanceGuid);
            try
            {
                var axes = joystick.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis);
                foreach (var axis in axes)
                    joystick.GetObjectPropertiesById(axis.ObjectId).Range = new InputRange(0, PedalConfig.RawMax);
                SimHub.Logging.Current.Info($"[SimsonnPedals] {device.ProductName.Trim()} axes: {string.Join(", ", axes.Select(a => a.Name))}");
                joystick.SetNotification(reportSignal);
                joystick.Acquire();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[SimsonnPedals] Could not open {device.ProductName}", ex);
                SafeDispose(joystick);
                return null;
            }

            DeviceName = device.ProductName.Trim();
            SimHub.Logging.Current.Info($"[SimsonnPedals] Connected to {DeviceName}");
            return joystick;
        }

        /// <summary>Sweep output for the elapsed time, or NaN once it is over.</summary>
        private double SweepValue(double elapsedMs)
        {
            SweepCountdownSeconds = Math.Max(0, (SweepCountdownMs - elapsedMs) / 1000);
            if (elapsedMs < SweepCountdownMs) return 0;
            double t = (elapsedMs - SweepCountdownMs) / SweepStrokeMs;
            if (t >= SweepStrokes) return double.NaN;
            double phase = t - Math.Floor(t);
            return phase < 0.5 ? phase * 2 : (1 - phase) * 2;
        }

        private static int ReadAxis(JoystickState state, InputAxis axis)
        {
            switch (axis)
            {
                case InputAxis.X: return state.X;
                case InputAxis.Y: return state.Y;
                case InputAxis.Z: return state.Z;
                case InputAxis.RotationX: return state.RotationX;
                case InputAxis.RotationY: return state.RotationY;
                case InputAxis.RotationZ: return state.RotationZ;
                case InputAxis.Slider0: return state.Sliders[0];
                case InputAxis.Slider1: return state.Sliders[1];
                default: return 0;
            }
        }

        private static void SafeDispose(Joystick joystick)
        {
            if (joystick == null) return;
            try
            {
                joystick.Unacquire();
                joystick.Dispose();
            }
            catch (Exception)
            {
                // Device already gone.
            }
        }
    }
}
