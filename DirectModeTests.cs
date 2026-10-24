using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SimsonnPedals
{
    public sealed class DirectModeTestResult
    {
        public string Summary { get; set; }
        public string ReportPath { get; set; }
    }

    /// <summary>
    /// Phase D1 rig checks for direct mode (no vJoy): does a settings write apply live without the pedals dropping off
    /// USB, does the firmware throttle cut work, and does <c>damping_max</c> change the brake live. The firmware code says
    /// yes to all three (research/direct-mode.md); these confirm it. Each test changes the pedals' settings only for a few
    /// seconds and puts them back. Pedal values are watched through the plugin's own DirectInput reading, which shows the
    /// firmware's processed axes (0–65535), independent of the plugin's shaping.
    /// </summary>
    public static class DirectModeTests
    {
        private const double FullScale = 65535;

        /// <summary>
        /// Writes a block with the throttle reaching 100 % at half its travel, then the original back. The user holds the
        /// throttle about halfway: if the write applies live, the throttle jumps to about twice its value.
        /// </summary>
        public static DirectModeTestResult LiveWrite(PedalEngine engine, int vendorId, int productId, Action<string> progress)
        {
            var report = Start("live settings write", engine);
            var original = PedalConfigReader.Read(vendorId, productId, "before direct-mode live write test");
            if (!original.Success) return Finish("live-write", "Couldn't read the settings, nothing was changed: " + original.Summary, report);
            if (!PedalSettingsLayout.IsLayoutVerified(original.Data))
                return Finish("live-write", $"Unknown settings layout (firmware {PedalSettingsLayout.FirmwareVersionText(original.Data)}), nothing was changed.", report);

            int axis = PedalSettingsLayout.ThrottleAxis(original.Data);
            var throttle = PedalSettingsLayout.ReadAxis(original.Data, axis);
            int half = throttle.OutLow + (throttle.OutHigh - throttle.OutLow) / 2;
            var test = PedalSettingsLayout.WithOutputEnds(original.Data, axis, throttle.OutLow, half);
            report.AppendLine($"Firmware {PedalSettingsLayout.FirmwareVersionText(original.Data)}, throttle axis {axis}: output ends {throttle.OutLow}…{throttle.OutHigh}, test high end {half}");

            using (var sampler = new Sampler(engine))
            {
                progress("Hold the throttle about halfway and keep it there…");
                Thread.Sleep(2000);
                double before = sampler.Average(s => s.Throttle, 1000);
                int disconnects = engine.DisconnectCount;
                progress("Writing: throttle 100 % at half travel. Keep holding…");
                sampler.Mark("write test settings");
                var written = PedalConfigWriter.Write(vendorId, productId, original.Data, test, "direct test half travel");
                report.AppendLine("Test write: " + written.Summary);
                Thread.Sleep(1000);
                double after = sampler.Average(s => s.Throttle, 1000);
                double? seenAfterMs = sampler.FirstAfterMark(s => s.Throttle >= Math.Min(FullScale * 0.97, before * 1.5 + FullScale * 0.03));
                int droppedTest = engine.DisconnectCount - disconnects;

                progress("Putting the settings back. Keep holding…");
                sampler.Mark("write original settings");
                var restored = written.Sent
                    ? PedalConfigWriter.Write(vendorId, productId, test, original.Data, "direct test restore")
                    : new PedalConfigWriteResult { Success = true, Summary = "Nothing to put back: the test write wasn't sent." };
                report.AppendLine("Restore: " + restored.Summary);
                Thread.Sleep(1000);
                double back = sampler.Average(s => s.Throttle, 1000);
                int droppedTotal = engine.DisconnectCount - disconnects;
                sampler.AppendTrace(report);

                var lines = new List<string>();
                if (!written.Sent) lines.Add("The test settings weren't written: " + written.Summary);
                else if (before < FullScale * 0.1 || before > FullScale * 0.6)
                    lines.Add($"Inconclusive: the throttle was at {Pct(before)} before the write, not about halfway.");
                else if (after >= Math.Min(FullScale * 0.97, before * 1.5 + FullScale * 0.03))
                    lines.Add($"Applied live: throttle {Pct(before)} → {Pct(after)} while held{(seenAfterMs == null ? "" : $", {seenAfterMs:0} ms after the write began")}.");
                else
                    lines.Add($"Not applied live: throttle {Pct(before)} → {Pct(after)} while held.");
                lines.Add(droppedTotal == 0 ? "The pedals stayed connected throughout." : $"The pedals dropped off {droppedTotal} time(s) ({droppedTest} during the test write).");
                lines.Add(restored.Success ? $"Settings put back and verified (throttle {Pct(back)})." : "PUTTING THE SETTINGS BACK FAILED: " + restored.Summary + $" The original is in {Path.GetFileName(original.BinaryPath)}.");
                return Finish("live-write", string.Join(" ", lines), report);
            }
        }

        /// <summary>
        /// Switches the firmware throttle cut on at <paramref name="thresholdTenths"/> for 10 s while the user holds the
        /// throttle and presses the brake past the level, then sets it back to what it was.
        /// </summary>
        public static DirectModeTestResult FirmwareBrakeCut(PedalEngine engine, int vendorId, int productId, int thresholdTenths, Action<string> progress)
        {
            var report = Start($"firmware throttle cut at {thresholdTenths / 10.0:0.#} %", engine);
            var before = PedalStatusReader.ReadBrakeCutoff(vendorId, productId, report);
            if (before == null) return Finish("brake-cut", "Couldn't read the firmware throttle cut, nothing was changed.", report);

            using (var sampler = new Sampler(engine))
            {
                var on = PedalCommandWriter.SetBrakeCutoff(vendorId, productId, true, thresholdTenths, report);
                if (on == null || !on.Value.Enabled)
                    return Finish("brake-cut", "The pedals didn't switch their throttle cut on: " + (on?.ToString() ?? "no answer"), report);
                sampler.Mark("cut on");
                for (int s = 10; s > 0; s--)
                {
                    progress($"Firmware cut on at {thresholdTenths / 10.0:0.#} %: hold the throttle down, then press the brake past that and back, a few times ({s} s)…");
                    Thread.Sleep(1000);
                }
                var back = PedalCommandWriter.SetBrakeCutoff(vendorId, productId, before.Value.Enabled, Math.Max(1, Math.Min(PedalCommandWriter.MaxBrakeCutoffTenths, before.Value.ThresholdTenths)), report);
                sampler.Mark("cut back");
                sampler.AppendTrace(report);

                double level = thresholdTenths / 1000.0 * FullScale;
                var during = sampler.AfterMark("cut on").TakeWhile(x => x.Mark != "cut back").ToList();
                var below = during.Where(x => x.Brake < level * 0.8).ToList();
                var above = during.Where(x => x.Brake > level * 1.2 + FullScale * 0.02).ToList();
                var lines = new List<string>();
                if (below.Count == 0 || above.Count == 0 || below.Max(x => x.Throttle) < FullScale * 0.2)
                    lines.Add($"Inconclusive: needs the throttle held (seen up to {Pct(below.Select(x => (double)x.Throttle).DefaultIfEmpty(0).Max())}) with the brake both below and above {thresholdTenths / 10.0:0.#} %.");
                else
                {
                    // A sample or two right as the brake crosses the level can still show the throttle up (the two axes
                    // aren't read at the same instant), so judge by the share of samples above the level that were cut.
                    double cutShare = above.Count(x => x.Throttle < FullScale * 0.02) / (double)above.Count;
                    lines.Add(cutShare >= 0.9
                        ? $"Works: throttle up to {Pct(below.Max(x => x.Throttle))} with the brake below the level, 0 above it ({cutShare:P0} of samples above the level)."
                        : $"Didn't cut: the throttle stayed up in {1 - cutShare:P0} of the samples with the brake above the level.");
                    // Where it cut: the lowest brake value seen with the throttle at 0 right after it was up.
                    double? cutAt = during.Zip(during.Skip(1), (a, b) => a.Throttle > FullScale * 0.1 && b.Throttle < FullScale * 0.02 ? (double?)b.Brake : null)
                        .Where(v => v != null).DefaultIfEmpty(null).Min();
                    if (cutAt != null) lines.Add($"It cut at about {Pct(cutAt.Value)} brake.");
                }
                lines.Add(back != null && back.Value.Enabled == before.Value.Enabled ? "Set back: " + back : "SETTING IT BACK FAILED; use Set up pedals. " + (back?.ToString() ?? ""));
                return Finish("brake-cut", string.Join(" ", lines), report);
            }
        }

        /// <summary>
        /// Halves the brake's max force with <c>damping_max</c> while the user holds the brake steady: if it applies live,
        /// the brake reading about doubles. Reads the block to see whether it was saved, then puts the original back.
        /// </summary>
        public static DirectModeTestResult LiveMaxForce(PedalEngine engine, int vendorId, int productId, Action<string> progress)
        {
            var report = Start("live brake max force (damping_max)", engine);
            var original = PedalConfigReader.Read(vendorId, productId, "before direct-mode max force test");
            if (!original.Success) return Finish("max-force", "Couldn't read the settings, nothing was changed: " + original.Summary, report);
            if (!PedalSettingsLayout.IsLayoutVerified(original.Data))
                return Finish("max-force", $"Unknown settings layout (firmware {PedalSettingsLayout.FirmwareVersionText(original.Data)}), nothing was changed.", report);

            int axis = PedalSettingsLayout.BrakeAxis(original.Data);
            var brake = PedalSettingsLayout.ReadAxis(original.Data, axis);
            int halfRawMax = brake.RawMin + (brake.RawMax - brake.RawMin) / 2;
            int halfOutHigh = brake.RawMin + (brake.OutHigh - brake.RawMin) / 2;
            report.AppendLine($"Brake axis {axis}: raw {brake.RawMin}…{brake.RawMax}, output ends {brake.OutLow}…{brake.OutHigh}; test raw max {halfRawMax}, high end {halfOutHigh}");

            using (var sampler = new Sampler(engine))
            {
                int disconnects = engine.DisconnectCount;
                progress("Press the brake lightly (about 20–40 %) and hold it steady…");
                Thread.Sleep(2500);
                double before = sampler.Average(s => s.Brake, 1000);
                progress("Halving the brake's max force. Keep holding…");
                sampler.Mark("half force");
                bool sent = PedalCommandWriter.SetDampingMax(vendorId, productId, halfRawMax, halfOutHigh, report);
                Thread.Sleep(1500);
                double after = sampler.Average(s => s.Brake, 1000);
                double? seenAfterMs = sampler.FirstAfterMark(s => s.Brake >= Math.Min(FullScale * 0.97, before * 1.5 + FullScale * 0.03));

                progress("Reading the settings to see whether the change was saved…");
                var changed = PedalConfigReader.Read(vendorId, productId, "direct-mode max force test, half force");
                bool saved = changed.Success && PedalSettingsLayout.ReadAxis(changed.Data, axis).RawMax == halfRawMax;

                progress("Putting the max force back…");
                sampler.Mark("restore");
                PedalCommandWriter.SetDampingMax(vendorId, productId, brake.RawMax, brake.OutHigh, report);
                Thread.Sleep(1000);
                var final = PedalConfigReader.Read(vendorId, productId, "after direct-mode max force test");
                string restore;
                if (!final.Success) restore = "COULDN'T READ BACK AFTER RESTORING: check with Read pedal settings.";
                else if (final.Data.SequenceEqual(original.Data)) restore = "Settings back exactly as before.";
                else
                {
                    // The firmware recomputes the centre; if the original's wasn't the middle, write the original block back.
                    var rewrite = PedalConfigWriter.Write(vendorId, productId, final.Data, original.Data, "direct test max force restore");
                    restore = rewrite.Success ? "Settings back exactly as before (rewritten)." : "PUTTING THE SETTINGS BACK FAILED: " + rewrite.Summary;
                }
                int dropped = engine.DisconnectCount - disconnects;
                sampler.AppendTrace(report);

                var lines = new List<string>();
                if (!sent) lines.Add("The command couldn't be sent.");
                else if (before < FullScale * 0.08 || before > FullScale * 0.5)
                    lines.Add($"Inconclusive: the brake was at {Pct(before)} before the change, not a light steady press.");
                else if (after >= Math.Min(FullScale * 0.97, before * 1.5 + FullScale * 0.03))
                    lines.Add($"Applied live: brake {Pct(before)} → {Pct(after)} at the same press{(seenAfterMs == null ? "" : $", after {seenAfterMs:0} ms")}.");
                else
                    lines.Add($"Not applied live: brake {Pct(before)} → {Pct(after)}.");
                lines.Add(changed.Success ? (saved ? "Saved in the pedals' settings." : "Not in the stored settings.") : "Couldn't read the settings in between.");
                lines.Add(dropped == 0 ? "The pedals stayed connected." : $"The pedals dropped off {dropped} time(s).");
                lines.Add(restore);
                return Finish("max-force", string.Join(" ", lines), report);
            }
        }

        /// <summary>
        /// Phase D3: how long the pedals' joystick reports stop during a settings read and during a write on its own.
        /// Listens to the joystick interface (MI_00) directly with HidSharp, so the gaps are between USB reports, not
        /// value changes. Writes the block the pedals already hold, so nothing changes (one flash erase).
        /// </summary>
        public static DirectModeTestResult WritePause(int vendorId, int productId, Action<string> progress)
        {
            var report = new StringBuilder();
            report.AppendLine($"SIMSONN direct mode test (D3): joystick pause during a read and a write, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            var joystick = HidSharp.DeviceList.Local.GetHidDevices(vendorId, productId)
                .FirstOrDefault(d => d.DevicePath.IndexOf("&mi_00", StringComparison.OrdinalIgnoreCase) >= 0);
            if (joystick == null || !joystick.TryOpen(out HidSharp.HidStream stream))
                return Finish("write-pause", "Couldn't open the pedals' joystick interface.", report);

            var times = new List<double>();
            var clock = Stopwatch.StartNew();
            bool stop = false;
            var listener = new Thread(() =>
            {
                var buffer = new byte[joystick.GetMaxInputReportLength()];
                stream.ReadTimeout = 50;
                while (!Volatile.Read(ref stop))
                {
                    try
                    {
                        if (stream.Read(buffer) > 0) lock (times) times.Add(clock.Elapsed.TotalMilliseconds);
                    }
                    catch (TimeoutException) { }
                    catch (IOException) { Thread.Sleep(20); }
                }
            }) { IsBackground = true, Name = "SimsonnPedals D3 listener" };

            using (stream)
            {
                listener.Start();
                progress("Press the brake lightly and hold it. Listening to the pedals…");
                Thread.Sleep(2000);
                double readStart = clock.Elapsed.TotalMilliseconds;
                progress("Reading the settings (keep holding)…");
                var read = PedalConfigReader.Read(vendorId, productId, "direct test pause");
                double readEnd = clock.Elapsed.TotalMilliseconds;
                Thread.Sleep(1500);
                string write;
                double writeStart = clock.Elapsed.TotalMilliseconds, writeEnd = writeStart;
                if (!read.Success) write = "skipped (the read failed: " + read.Summary + ")";
                else
                {
                    progress("Writing the same settings back (keep holding)…");
                    var result = PedalConfigWriter.WriteKnown(vendorId, productId, read.Data, "direct test pause");
                    writeEnd = clock.Elapsed.TotalMilliseconds;
                    write = result.Summary;
                }
                Thread.Sleep(1500);
                Volatile.Write(ref stop, true);
                listener.Join(500);

                List<double> all;
                lock (times) all = times.ToList();
                string Gap(double from, double to)
                {
                    // Reports from just before the span to just after it, so a pause at either edge counts.
                    var inside = all.Where(t => t >= from - 200 && t <= to + 200).ToList();
                    if (inside.Count < 2) return "no reports";
                    double gap = inside.Zip(inside.Skip(1), (a, b) => b - a).Max();
                    return $"longest gap {gap:0} ms";
                }
                var baseline = all.Where(t => t < readStart).ToList();
                double rate = baseline.Count < 2 ? 0 : (baseline.Count - 1) * 1000 / (baseline.Last() - baseline.First());
                string summary = $"Joystick reports: {rate:0}/s, {Gap(0, readStart - 200)} normally; during a settings read ({readEnd - readStart:0} ms) " +
                                 $"{Gap(readStart, readEnd)}; during a write ({writeEnd - writeStart:0} ms) {Gap(writeStart, writeEnd)}. Write: {write}";
                report.AppendLine($"Read {readStart:0}–{readEnd:0} ms, write {writeStart:0}–{writeEnd:0} ms");
                report.AppendLine($"{all.Count} joystick reports. Gaps of 15 ms or more (from, length):");
                foreach (var gap in all.Zip(all.Skip(1), (a, b) => (a, b - a)).Where(g => g.Item2 >= 15))
                    report.AppendLine($"  {gap.Item1,7:0} ms: {gap.Item2:0} ms");
                return Finish("write-pause", summary, report);
            }
        }

        private static string Pct(double value) => $"{value / FullScale * 100:0} %";

        private static StringBuilder Start(string name, PedalEngine engine)
        {
            var report = new StringBuilder();
            report.AppendLine($"SIMSONN direct mode test (D1): {name}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Pedals: {(engine.DeviceConnected ? engine.DeviceName : "not connected")}, disconnects so far {engine.DisconnectCount}");
            report.AppendLine();
            return report;
        }

        private static DirectModeTestResult Finish(string label, string summary, StringBuilder report)
        {
            var result = new DirectModeTestResult { Summary = summary };
            try
            {
                Directory.CreateDirectory(PedalConfigReader.OutputFolder);
                report.Insert(0, $"Result: {summary}{Environment.NewLine}");
                result.ReportPath = Path.Combine(PedalConfigReader.OutputFolder, $"direct-test-{DateTime.Now:yyyyMMdd-HHmmss}-{label}.txt");
                File.WriteAllText(result.ReportPath, report.ToString());
            }
            catch (Exception ex)
            {
                result.Summary += $" (Could not save the report: {ex.Message})";
            }
            SimHub.Logging.Current.Info($"[SimsonnPedals] Direct mode test {label}: {result.Summary}");
            return result;
        }

        /// <summary>Records the engine's raw throttle and brake every 10 ms on its own thread, with marks for events.</summary>
        private sealed class Sampler : IDisposable
        {
            public sealed class Sample
            {
                public double Ms;
                public int Throttle, Brake, Disconnects;
                public bool Connected;
                public string Mark;
            }

            private readonly PedalEngine engine;
            private readonly List<Sample> samples = new List<Sample>();
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly Thread thread;
            private volatile bool stop;
            private string pendingMark;
            private double markMs = double.NaN;

            public Sampler(PedalEngine engine)
            {
                this.engine = engine;
                thread = new Thread(Run) { IsBackground = true, Name = "SimsonnPedals D1 sampler" };
                thread.Start();
            }

            private void Run()
            {
                while (!stop)
                {
                    var s = new Sample
                    {
                        Ms = clock.Elapsed.TotalMilliseconds,
                        Throttle = engine.Throttle.Raw,
                        Brake = engine.Brake.Raw,
                        Connected = engine.DeviceConnected,
                        Disconnects = engine.DisconnectCount,
                    };
                    lock (samples)
                    {
                        s.Mark = pendingMark;
                        pendingMark = null;
                        samples.Add(s);
                    }
                    Thread.Sleep(10);
                }
            }

            public void Mark(string name)
            {
                lock (samples)
                {
                    pendingMark = name;
                    markMs = clock.Elapsed.TotalMilliseconds;
                }
            }

            private List<Sample> Snapshot()
            {
                lock (samples) return samples.ToList();
            }

            /// <summary>Average over the last <paramref name="ms"/> milliseconds of connected samples.</summary>
            public double Average(Func<Sample, int> value, double ms)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                var recent = Snapshot().Where(s => s.Connected && s.Ms >= now - ms).ToList();
                return recent.Count == 0 ? 0 : recent.Average(value);
            }

            /// <summary>Milliseconds from the last mark to the first sample matching <paramref name="test"/>, or null.</summary>
            public double? FirstAfterMark(Func<Sample, bool> test)
            {
                double mark = markMs;
                var hit = Snapshot().FirstOrDefault(s => s.Ms >= mark && s.Connected && test(s));
                return hit == null ? (double?)null : hit.Ms - mark;
            }

            public IEnumerable<Sample> AfterMark(string name) => Snapshot().SkipWhile(s => s.Mark != name);

            /// <summary>Every 50 ms (and every mark or connection change) as a table, for reading the test afterwards.</summary>
            public void AppendTrace(StringBuilder report)
            {
                report.AppendLine();
                report.AppendLine("Trace (ms, throttle raw, brake raw, connected, disconnects, mark):");
                double next = 0;
                Sample last = null;
                foreach (var s in Snapshot())
                {
                    bool changed = last != null && (s.Connected != last.Connected || s.Disconnects != last.Disconnects);
                    if (s.Ms >= next || s.Mark != null || changed)
                    {
                        report.AppendLine($"  {s.Ms,7:0} {s.Throttle,6} {s.Brake,6} {(s.Connected ? "yes" : "NO "),3} {s.Disconnects,3} {s.Mark}");
                        next = s.Ms + 50;
                    }
                    last = s;
                }
            }

            public void Dispose()
            {
                stop = true;
                thread.Join(500);
            }
        }
    }
}
