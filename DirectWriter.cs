using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimsonnPedals
{
    /// <summary>
    /// Direct mode (phases D3–D5): keeps the pedals' own settings in step with the active preset.
    ///
    /// Full writes go out when direct mode is switched on, when the preset switches (also Revert and Save as), on Save,
    /// and on Write to pedals; edits alone don't, since every write erases the pedals' settings flash. Writes go out from
    /// the block read once (kept up to date with every write), without reads around them.
    ///
    /// Every settings read or write freezes the pedals' joystick reports for 2 s (a firmware timer, research/direct-mode.md),
    /// so while a game runs, reads and writes wait until the car is stopped and throttle and brake are released (D4).
    ///
    /// Two things follow edits live, through report 6 commands that pause the pedals only 200 ms (D5): the brake's
    /// "100% at" (<c>damping_max</c>, when nothing else changed) and the preset's "cut throttle when braking", which in
    /// direct mode is the firmware's own cut (<c>brakeCutoff</c>, at most 50 %).
    ///
    /// Work runs one request at a time on a worker; requests made meanwhile are merged into one more pass.
    /// </summary>
    public sealed class DirectWriter
    {
        private const string LabelPrefix = "direct mode";
        private const double RetryAfterSeconds = 5;
        private const int LiveDelayMs = 500;
        private const double StoppedKmh = 1;
        private const double ReleasedOutput = 0.02;

        private readonly SimsonnPedalsPlugin plugin;
        private readonly object gate = new object();
        private readonly Timer liveTimer;
        private volatile byte[] pedalsBlock;
        private PedalStatusReader.BrakeCutoff? pedalsCutoff;
        private bool running, pending, pendingVerify, pendingFull;
        private string pendingReason;
        private int paused;
        private int knownDisconnects;
        private DateTime retryAt = DateTime.MaxValue;

        // A pass that had to wait for a safe moment (D4); Tick sends it again once it's safe.
        private bool deferred, deferredVerify, deferredFull;
        private string deferredReason;

        public DirectWriter(SimsonnPedalsPlugin plugin)
        {
            this.plugin = plugin;
            knownDisconnects = plugin.Engine.DisconnectCount;
            liveTimer = new Timer(_ => Request("live edit", full: false), null, Timeout.Infinite, Timeout.Infinite);
            PedalConfigWriter.Written += OnWritten;
        }

        public void Dispose()
        {
            PedalConfigWriter.Written -= OnWritten;
            liveTimer.Dispose();
        }

        public bool IsDirect => plugin.Settings.OutputMode == OutputMode.Direct;

        /// <summary>One line for the page: what the pedals hold, or what's happening.</summary>
        public string Status { get; private set; } = "";

        public bool Busy { get { lock (gate) return running; } }

        /// <summary>True while a write waits for the car to stop and the pedals to be released.</summary>
        public bool Waiting { get { lock (gate) return deferred; } }

        /// <summary>When the last direct-mode write went out, or null.</summary>
        public DateTime? LastWrite { get; private set; }

        /// <summary>
        /// True if the pedals hold <paramref name="preset"/> as it is now (settings and firmware brake cut), false if not,
        /// null if their settings haven't been read yet.
        /// </summary>
        public bool? Holds(PedalPreset preset, bool clutch)
        {
            var block = pedalsBlock;
            if (block == null) return null;
            var export = PedalExport.ToBlock(block, preset, clutch);
            var cutoff = pedalsCutoff;
            return export.Success && !export.Changed && (cutoff == null || CutoffMatches(cutoff.Value, preset.Assist));
        }

        /// <summary>
        /// Asks for a pass. <paramref name="full"/>: write the active preset if it differs (otherwise only the live parts:
        /// brake "100% at" and the brake cut). <paramref name="verify"/>: read the pedals first.
        /// </summary>
        public void Request(string reason, bool verify = false, bool full = true)
        {
            if (!IsDirect) return;
            lock (gate)
            {
                pending = true;
                pendingReason = reason;
                pendingVerify |= verify;
                pendingFull |= full;
                if (running || paused > 0) return;
                running = true;
            }
            Task.Run(Loop);
        }

        /// <summary>An edit of the active preset: the live parts follow half a second after the last change.</summary>
        public void LiveEdit()
        {
            if (IsDirect) liveTimer.Change(LiveDelayMs, Timeout.Infinite);
        }

        /// <summary>Forget what the pedals hold, so the next pass reads them first.</summary>
        public void Invalidate()
        {
            pedalsBlock = null;
            pedalsCutoff = null;
        }

        /// <summary>
        /// Holds automatic writes while another pedal operation runs (tests, calibration, firmware update). Releasing the
        /// hold re-reads the pedals and writes the preset again if direct mode is on.
        /// </summary>
        public void Hold(bool hold)
        {
            if (Interlocked.Exchange(ref paused, hold ? 1 : 0) == (hold ? 1 : 0) || hold) return;
            Invalidate();
            Request("after another pedal operation");
        }

        /// <summary>
        /// Null when the pedals may be read or written now; otherwise why not. While a game runs that needs the car stopped
        /// and throttle and brake released, since the pedals freeze for 2 s.
        /// </summary>
        public string WaitReason()
        {
            if (!plugin.IsGameRunning) return null;
            if (plugin.CurrentSpeedKmh >= StoppedKmh) return "the car is moving";
            var engine = plugin.Engine;
            if (engine.Throttle.Output > ReleasedOutput || engine.Brake.Output > ReleasedOutput) return "release throttle and brake";
            return null;
        }

        /// <summary>Called from SimHub's data loop: sends a waiting pass once it's safe, retries failures, re-reads after a replug.</summary>
        public void Tick()
        {
            if (!IsDirect || !plugin.Engine.DeviceConnected) return;
            int disconnects = plugin.Engine.DisconnectCount;
            if (disconnects != knownDisconnects)
            {
                knownDisconnects = disconnects;
                Invalidate();
                Request("pedals reconnected");
                return;
            }
            bool send;
            string reason;
            bool verify, full;
            lock (gate)
            {
                send = deferred && WaitReason() == null;
                reason = deferredReason;
                verify = deferredVerify;
                full = deferredFull;
                if (send) deferred = false;
            }
            if (send) Request(reason, verify, full);
            else if (DateTime.UtcNow >= retryAt)
            {
                retryAt = DateTime.MaxValue;
                Request("retry");
            }
        }

        /// <summary>
        /// Leaving direct mode: sets the pedals back to neutral (linear, full range, filter off, the brake's max force
        /// kept) as Set up pedals does, and switches the firmware brake cut off, so vJoy mode shapes from a clean start.
        /// Returns a line for the page.
        /// </summary>
        public string WriteNeutral()
        {
            int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
            string cut = "";
            var off = PedalCommandWriter.SwitchThrottleCutOff(vid, pid);
            if (!off.Success) cut = " The firmware brake cut couldn't be switched off: use Set up pedals.";
            var read = PedalConfigReader.Read(vid, pid, "leaving direct mode");
            if (!read.Success) return "Couldn't read the pedals' settings: " + read.Summary + cut;
            double? kg = PedalSettingsLayout.BrakeMaxForceKg(read.Data);
            if (kg == null) return $"Firmware {PedalSettingsLayout.FirmwareVersionText(read.Data)} isn't supported here." + cut;
            var neutral = PedalSettingsLayout.BaseSetup(read.Data, kg.Value);
            if (neutral.SequenceEqual(read.Data)) return "The pedals were already neutral." + cut;
            var result = PedalConfigWriter.Write(vid, pid, read.Data, neutral, LabelPrefix + " off, neutral");
            if (result.Success) CountWrite();
            return (result.Success ? $"The pedals are back to neutral (linear, no dead zone, brake max force {kg:0.#} kg)." : result.Summary) + cut;
        }

        private void Loop()
        {
            while (true)
            {
                string reason;
                bool verify, full;
                lock (gate)
                {
                    if (!pending || paused > 0 || !IsDirect)
                    {
                        running = false;
                        return;
                    }
                    pending = false;
                    reason = pendingReason;
                    verify = pendingVerify;
                    full = pendingFull;
                    pendingVerify = pendingFull = false;
                }
                try
                {
                    Pass(reason, verify, full);
                }
                catch (Exception ex)
                {
                    Invalidate();
                    Fail("Writing to the pedals failed: " + ex.Message);
                    SimHub.Logging.Current.Error("[SimsonnPedals] Direct mode write failed", ex);
                }
            }
        }

        private void Pass(string reason, bool verify, bool full)
        {
            var settings = plugin.Settings;
            int vid = settings.VendorId, pid = settings.ProductId;
            string name = settings.ActivePresetName;
            var preset = plugin.ActivePreset.Clone();
            bool clutch = settings.ClutchEnabled;
            // Every step below talks to the pedals, which freezes them; while driving, wait for a safe moment.
            bool Wait()
            {
                string why = WaitReason();
                if (why == null) return false;
                lock (gate)
                {
                    deferred = true;
                    deferredReason = reason;
                    deferredVerify |= verify;
                    deferredFull |= full;
                }
                Status = $"Waiting to send {name} to the pedals: {why} (they pause for 2 s while it's written).";
                return true;
            }

            if (pedalsBlock == null || verify)
            {
                // Live edits need to know what the pedals hold; without it they wait for the next full pass.
                if (!full && !verify) return;
                if (Wait()) return;
                Status = "Reading the pedals' settings…";
                var read = PedalConfigReader.Read(vid, pid, verify ? "direct mode check" : null);
                if (!read.Success)
                {
                    Fail("Couldn't read the pedals' settings: " + read.Summary);
                    return;
                }
                if (!PedalSettingsLayout.IsLayoutVerified(read.Data))
                {
                    Fail($"Direct mode doesn't know firmware {PedalSettingsLayout.FirmwareVersionText(read.Data)}'s settings layout.");
                    return;
                }
                pedalsBlock = read.Data;
                pedalsCutoff = PedalStatusReader.ReadBrakeCutoff(vid, pid, new StringBuilder());
            }

            var export = PedalExport.ToBlock(pedalsBlock, preset, clutch);
            if (!export.Success)
            {
                Fail(export.ToString());
                return;
            }
            string done = null;
            if (export.Changed && PedalExport.OnlyBrakeHighEnd(pedalsBlock, export.Block))
            {
                // Only the brake's "100% at" changed: the live command does it with a 200 ms pause instead of 2 s.
                if (Wait()) return;
                int brake = PedalSettingsLayout.BrakeAxis(pedalsBlock);
                var before = PedalSettingsLayout.ReadAxis(pedalsBlock, brake);
                var after = PedalSettingsLayout.ReadAxis(export.Block, brake);
                Status = $"Sending {name}'s brake 100% to the pedals…";
                if (!PedalCommandWriter.SetDampingMax(vid, pid, before.RawMax, after.OutHigh, new StringBuilder()))
                {
                    Invalidate();
                    Fail("Couldn't send the brake's 100% to the pedals.");
                    return;
                }
                var updated = (byte[])pedalsBlock.Clone();
                int o = PedalSettingsLayout.AxisOffset(brake);
                Array.Copy(export.Block, o + 2, updated, o + 2, 4);
                pedalsBlock = updated;
                done = $"brake 100% sent live {DateTime.Now:HH:mm:ss}";
                SimHub.Logging.Current.Info($"[SimsonnPedals] Direct mode: brake high end {before.OutHigh} → {after.OutHigh} live ({reason}).");
            }
            else if (export.Changed && (full || verify))
            {
                if (Wait()) return;
                Status = $"Writing {name} to the pedals…";
                var result = PedalConfigWriter.WriteKnown(vid, pid, export.Block, $"{LabelPrefix} {name} ({reason})");
                if (!result.Success)
                {
                    Invalidate();
                    Fail($"Couldn't write {name}: {result.Summary}");
                    return;
                }
                CountWrite();
                LastWrite = DateTime.Now;
                done = $"written {LastWrite:HH:mm:ss}";
                SimHub.Logging.Current.Info($"[SimsonnPedals] Direct mode: wrote {name} ({reason}). {export}");
            }

            // The preset's "cut throttle when braking" is the firmware's own cut in direct mode.
            var cutoff = pedalsCutoff;
            if (cutoff != null && !CutoffMatches(cutoff.Value, preset.Assist))
            {
                if (Wait()) return;
                bool on = preset.Assist.BrakeCut;
                int tenths = on ? CutoffTenths(preset.Assist) : Math.Max(1, Math.Min(PedalCommandWriter.MaxBrakeCutoffTenths, cutoff.Value.ThresholdTenths));
                var set = PedalCommandWriter.SetBrakeCutoff(vid, pid, on, tenths, new StringBuilder());
                pedalsCutoff = set;
                if (set == null || set.Value.Enabled != on)
                {
                    Fail("Couldn't set the pedals' brake cut: " + (set?.ToString() ?? "no answer"));
                    return;
                }
                done = (done == null ? "" : done + ", ") + (on ? $"brake cut at {tenths / 10.0:0.#} %" : "brake cut off");
            }

            if (done != null) lastDone = (name, done);
            string when = lastDone.Name == name && lastDone.What != null ? $" ({lastDone.What})" : "";
            Status = export.Changed ? $"The pedals hold {name} as last saved{when}."
                : verify ? $"Checked: the pedals hold {name}{when}." : $"The pedals hold {name}{when}.";
        }

        // What was last sent, for the status line while nothing new needs sending.
        private (string Name, string What) lastDone;

        private static int CutoffTenths(ShiftAssist assist) =>
            Math.Max(1, Math.Min(PedalCommandWriter.MaxBrakeCutoffTenths, (int)Math.Round(assist.BrakeCutPercent * 10)));

        private static bool CutoffMatches(PedalStatusReader.BrakeCutoff cutoff, ShiftAssist assist) =>
            assist.BrakeCut ? cutoff.Enabled && cutoff.ThresholdTenths == CutoffTenths(assist) : !cutoff.Enabled;

        private void Fail(string message)
        {
            Status = message;
            retryAt = DateTime.UtcNow.AddSeconds(RetryAfterSeconds);
        }

        private void CountWrite()
        {
            plugin.Settings.DirectWriteCount++;
            plugin.RequestSave();
        }

        /// <summary>Any write keeps the known block current; one not made here also gets the preset written again.</summary>
        private void OnWritten(string label, byte[] block)
        {
            pedalsBlock = block;
            if (!label.StartsWith(LabelPrefix, StringComparison.Ordinal)) Request("settings written by " + label);
        }
    }
}
