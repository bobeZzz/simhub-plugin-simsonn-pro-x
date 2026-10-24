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

    /// <summary>What is overriding the throttle right now (see <see cref="ShiftAssist"/>).</summary>
    public enum ThrottleOverride { None, ShiftCut, Blip, BrakeCut }

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

        // Paddle presses older than this when the loop sees them (pedals were away) are dropped.
        private const double ShiftRequestMaxAgeMs = 100;
        // The brake cut lets go a little below its level, so a brake resting on the level doesn't flicker the throttle.
        private const double BrakeCutHysteresis = 0.02;

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long upshiftAtTicks, downshiftAtTicks;
        private double cutEndMs = double.NegativeInfinity, blipEndMs = double.NegativeInfinity;
        private bool brakeCutOn;

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

        /// <summary>Upshift paddle pressed (any thread): cuts the throttle if the preset asks for it.</summary>
        public void Upshift()
        {
            Interlocked.Exchange(ref upshiftAtTicks, Math.Max(1, clock.ElapsedTicks));
            Interlocked.Increment(ref upshiftCount);
        }

        /// <summary>Downshift paddle pressed (any thread): blips the throttle if the preset asks for it.</summary>
        public void Downshift()
        {
            Interlocked.Exchange(ref downshiftAtTicks, Math.Max(1, clock.ElapsedTicks));
            Interlocked.Increment(ref downshiftCount);
        }

        private int upshiftCount, downshiftCount;

        /// <summary>Paddle presses seen this session, so the page can show the buttons are mapped.</summary>
        public int UpshiftCount => upshiftCount;
        public int DownshiftCount => downshiftCount;

        private int disconnectCount;

        /// <summary>Times the pedals dropped off (USB or DirectInput) this session; the D1 tests watch it.</summary>
        public int DisconnectCount => disconnectCount;

        /// <summary>What overrides the throttle at the moment.</summary>
        public ThrottleOverride Override { get; private set; }

        /// <summary>Throttle as sent to vJoy, after any override (0–1).</summary>
        public double ThrottleSent { get; private set; }

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
                    Interlocked.Increment(ref disconnectCount);
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
                            ExitGuard.Start(s.VJoyDeviceId);
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

                        var assist = preset.Assist;
                        double upAt = TakeShiftRequest(ref upshiftAtTicks, now), downAt = TakeShiftRequest(ref downshiftAtTicks, now);
                        if (!double.IsNaN(upAt) && assist.UpshiftCut)
                        {
                            cutEndMs = upAt + assist.UpshiftCutMs;
                            blipEndMs = double.NegativeInfinity;
                        }
                        if (!double.IsNaN(downAt) && assist.DownshiftBlip)
                        {
                            blipEndMs = downAt + assist.BlipMs;
                            cutEndMs = double.NegativeInfinity;
                        }
                        // A throttle sweep is for binding: leave it alone.
                        if (sweepActive == SweepPedal.Throttle)
                        {
                            Override = ThrottleOverride.None;
                        }
                        else
                        {
                            throttle = ApplyAssist(assist, throttle, brake, now, cutEndMs, blipEndMs, ref brakeCutOn, out var overrideNow);
                            Override = overrideNow;
                        }
                        ThrottleSent = throttle;

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

        /// <summary>Time (ms on <see cref="clock"/>) of a paddle press not yet handled, or NaN if none or too old.</summary>
        private double TakeShiftRequest(ref long atTicks, double now)
        {
            long ticks = Interlocked.Exchange(ref atTicks, 0);
            if (ticks == 0) return double.NaN;
            double at = ticks * 1000.0 / Stopwatch.Frequency;
            return now - at <= ShiftRequestMaxAgeMs ? at : double.NaN;
        }

        /// <summary>
        /// Throttle (0–1) after the preset's overrides. Upshift cut wins over everything; a blip raises the throttle to
        /// its level even while braking (that's when downshifts happen); otherwise the brake cut holds it at 0 while the
        /// brake output is at or past its level.
        /// </summary>
        public static double ApplyAssist(ShiftAssist assist, double throttle, double brake, double now, double cutEndMs,
            double blipEndMs, ref bool brakeCutOn, out ThrottleOverride state)
        {
            if (assist.BrakeCut)
            {
                double level = assist.BrakeCutPercent / 100;
                brakeCutOn = brake >= level || (brakeCutOn && brake > level - Math.Min(BrakeCutHysteresis, level / 2));
            }
            else
            {
                brakeCutOn = false;
            }

            if (now < cutEndMs)
            {
                state = ThrottleOverride.ShiftCut;
                return 0;
            }
            if (now < blipEndMs)
            {
                state = ThrottleOverride.Blip;
                return Math.Max(throttle, assist.BlipPercent / 100);
            }
            if (brakeCutOn)
            {
                state = ThrottleOverride.BrakeCut;
                return 0;
            }
            state = ThrottleOverride.None;
            return throttle;
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
