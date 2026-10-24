using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SimsonnPedals
{
    /// <summary>
    /// Direct mode (phase D3): keeps the pedals' own settings in step with the active preset. Writes when direct mode
    /// is switched on, when the preset switches (also Revert and Save as), on Save, and on Write to pedals; edits alone
    /// don't write, since every write erases the pedals' settings flash. Writes go out from the block read once (and kept
    /// up to date with every write), without reading before or after: reads pause the pedals' joystick reports for about
    /// 2 s. Writes run one at a time on a worker; requests made meanwhile are merged into one more write.
    /// </summary>
    public sealed class DirectWriter
    {
        private const string LabelPrefix = "direct mode";
        private const double RetryAfterSeconds = 5;

        private readonly SimsonnPedalsPlugin plugin;
        private readonly object gate = new object();
        private volatile byte[] pedalsBlock;
        private bool running, pending, pendingVerify;
        private string pendingReason;
        private int paused;
        private int knownDisconnects;
        private DateTime retryAt = DateTime.MaxValue;

        public DirectWriter(SimsonnPedalsPlugin plugin)
        {
            this.plugin = plugin;
            knownDisconnects = plugin.Engine.DisconnectCount;
            PedalConfigWriter.Written += OnWritten;
        }

        public void Dispose() => PedalConfigWriter.Written -= OnWritten;

        public bool IsDirect => plugin.Settings.OutputMode == OutputMode.Direct;

        /// <summary>One line for the page: what the pedals hold, or what's happening.</summary>
        public string Status { get; private set; } = "";

        public bool Busy { get { lock (gate) return running; } }

        /// <summary>When the last direct-mode write went out, or null.</summary>
        public DateTime? LastWrite { get; private set; }

        /// <summary>
        /// True if the pedals hold <paramref name="preset"/> as it is now, false if not, null if their settings
        /// haven't been read yet.
        /// </summary>
        public bool? Holds(PedalPreset preset, bool clutch)
        {
            var block = pedalsBlock;
            if (block == null) return null;
            var export = PedalExport.ToBlock(block, preset, clutch);
            return export.Success && !export.Changed;
        }

        /// <summary>Asks for the active preset to be written (if it differs). <paramref name="verify"/> reads the pedals first.</summary>
        public void Request(string reason, bool verify = false)
        {
            if (!IsDirect) return;
            lock (gate)
            {
                pending = true;
                pendingReason = reason;
                pendingVerify |= verify;
                if (running || paused > 0) return;
                running = true;
            }
            Task.Run(Loop);
        }

        /// <summary>Forget what the pedals hold, so the next write reads them first.</summary>
        public void Invalidate() => pedalsBlock = null;

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

        /// <summary>Called from SimHub's data loop: retries after a failure, re-reads after the pedals reconnect.</summary>
        public void Tick()
        {
            if (!IsDirect || !plugin.Engine.DeviceConnected) return;
            int disconnects = plugin.Engine.DisconnectCount;
            if (disconnects != knownDisconnects)
            {
                knownDisconnects = disconnects;
                Invalidate();
                Request("pedals reconnected");
            }
            else if (DateTime.UtcNow >= retryAt)
            {
                retryAt = DateTime.MaxValue;
                Request("retry");
            }
        }

        /// <summary>
        /// Leaving direct mode: sets the pedals back to neutral (linear, full range, filter off, the brake's max force
        /// kept), as Set up pedals does, so vJoy mode shapes from a clean start. Returns a line for the page.
        /// </summary>
        public string WriteNeutral()
        {
            int vid = plugin.Settings.VendorId, pid = plugin.Settings.ProductId;
            var read = PedalConfigReader.Read(vid, pid, "leaving direct mode");
            if (!read.Success) return "Couldn't read the pedals' settings: " + read.Summary;
            double? kg = PedalSettingsLayout.BrakeMaxForceKg(read.Data);
            if (kg == null) return $"Firmware {PedalSettingsLayout.FirmwareVersionText(read.Data)} isn't supported here.";
            var neutral = PedalSettingsLayout.BaseSetup(read.Data, kg.Value);
            if (neutral.SequenceEqual(read.Data)) return "The pedals were already neutral.";
            var result = PedalConfigWriter.Write(vid, pid, read.Data, neutral, LabelPrefix + " off, neutral");
            if (result.Success) CountWrite();
            return result.Success ? $"The pedals are back to neutral (linear, no dead zone, brake max force {kg:0.#} kg)." : result.Summary;
        }

        private void Loop()
        {
            while (true)
            {
                string reason;
                bool verify;
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
                    pendingVerify = false;
                }
                try
                {
                    WriteOnce(reason, verify);
                }
                catch (Exception ex)
                {
                    pedalsBlock = null;
                    Fail("Writing to the pedals failed: " + ex.Message);
                    SimHub.Logging.Current.Error("[SimsonnPedals] Direct mode write failed", ex);
                }
            }
        }

        private void WriteOnce(string reason, bool verify)
        {
            var settings = plugin.Settings;
            int vid = settings.VendorId, pid = settings.ProductId;
            string name = settings.ActivePresetName;
            var preset = plugin.ActivePreset.Clone();
            bool clutch = settings.ClutchEnabled;

            if (pedalsBlock == null || verify)
            {
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
            }

            var export = PedalExport.ToBlock(pedalsBlock, preset, clutch);
            if (!export.Success)
            {
                Fail(export.ToString());
                return;
            }
            if (!export.Changed)
            {
                Status = verify ? $"Checked: the pedals hold {name}." : $"The pedals hold {name}.";
                return;
            }

            Status = $"Writing {name} to the pedals…";
            var result = PedalConfigWriter.WriteKnown(vid, pid, export.Block, $"{LabelPrefix} {name} ({reason})");
            if (!result.Success)
            {
                pedalsBlock = null;
                Fail($"Couldn't write {name}: {result.Summary}");
                return;
            }
            CountWrite();
            LastWrite = DateTime.Now;
            Status = $"The pedals hold {name} (written {LastWrite:HH:mm:ss}).";
            SimHub.Logging.Current.Info($"[SimsonnPedals] Direct mode: wrote {name} ({reason}). {export}");
        }

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
