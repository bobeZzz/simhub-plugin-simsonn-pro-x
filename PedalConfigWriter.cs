using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HidSharp;

namespace SimsonnPedals
{
    public sealed class PedalConfigWriteResult
    {
        public bool Success { get; set; }

        /// <summary>True once the last packet went out, i.e. the pedals may have saved something.</summary>
        public bool Sent { get; set; }

        public string Summary { get; set; }
        public string ReportPath { get; set; }
    }

    /// <summary>
    /// Writes a settings block to the pedals with FreeJoy's CONFIG_OUT exchange: the host sends [4, 0] and the
    /// pedals answer [4, 1], asking for packet 1; each packet [4, n, 62 bytes] is answered with [4, n + 1].
    /// Packets are only buffered until the last one arrives; the firmware then checks the version, saves to flash
    /// and restarts (USB drops for 1-2 s), or answers [4, 0xFE] and saves nothing on a version mismatch.
    /// So stopping at any point before the last packet leaves the stored settings untouched.
    /// </summary>
    public static class PedalConfigWriter
    {
        private const byte ReportIdConfigOut = 4;
        private const byte VersionMismatch = 0xFE;
        private const int Payload = PedalConfigReader.PayloadPerPacket;
        private const int AckTimeoutMs = 1000;
        private const int RestartWaitMs = 2500;
        private const int ReadBackAttempts = 5;

        // Layout fields checked before anything is sent (see research/pedal-settings-layout.md).
        private const int VendorIdOffset = 0x3EE;
        private const int ProductIdOffset = 0x3F0;

        /// <summary>
        /// Reads the settings, writes the same bytes back and reads them again. Proves the write path without
        /// changing anything; both reads are saved like any other dump.
        /// </summary>
        public static PedalConfigWriteResult RewriteTest(int vendorId, int productId)
        {
            var before = PedalConfigReader.Read(vendorId, productId, "before rewrite test");
            if (!before.Success)
                return new PedalConfigWriteResult { Summary = "Couldn't read the settings first, so nothing was written: " + before.Summary };
            return Write(vendorId, productId, before.Data, before.Data, "rewrite test");
        }

        /// <summary>
        /// Sends <paramref name="settings"/> to the pedals, but only if they still hold <paramref name="expectedCurrent"/>
        /// (so nothing changed them, e.g. Simsonn Manager, since that was read). Reads back and compares afterwards.
        /// </summary>
        public static PedalConfigWriteResult Write(int vendorId, int productId, byte[] expectedCurrent, byte[] settings, string label)
        {
            var report = new StringBuilder();
            report.AppendLine($"SIMSONN pedal settings write, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Label: {label}");
            report.AppendLine();

            string problem = Validate(vendorId, productId, expectedCurrent, settings);
            if (problem != null)
                return Finish(label, false, false, "Nothing was written: " + problem, report);

            // Re-read right before writing: the pedals must still hold what the caller based its changes on.
            var current = PedalConfigReader.Read(vendorId, productId, $"check before {label}");
            if (!current.Success)
                return Finish(label, false, false, "Nothing was written: couldn't re-read the settings. " + current.Summary, report);
            if (!current.Data.SequenceEqual(expectedCurrent))
                return Finish(label, false, false, "Nothing was written: the settings in the pedals changed since they were read. Read them again and retry.", report);

            var interfaces = PedalConfigReader.FindInterfaces(DeviceList.Local.GetHidDevices(vendorId, productId), ReportIdConfigOut, report);
            if (interfaces.Count != 1)
                return Finish(label, false, false, $"Nothing was written: expected one settings interface, found {interfaces.Count}.", report);

            int packets = settings.Length / Payload;
            report.AppendLine($"Writing {settings.Length} bytes in {packets} packets to {interfaces[0].DevicePath}");
            string error;
            bool sent;
            try
            {
                sent = SendAll(interfaces[0], settings, packets, report, out error);
            }
            catch (Exception ex)
            {
                // An exception can only come before the last packet: the firmware hasn't saved anything.
                return Finish(label, false, false, $"Nothing was saved: {ex.GetType().Name}: {ex.Message}", report);
            }
            if (!sent)
                return Finish(label, false, false, "Nothing was saved: " + error, report);
            if (error != null)
                return Finish(label, false, true, error, report);

            // The pedals restart to load the new settings; give them time, then check what they kept.
            report.AppendLine("Last packet sent; waiting for the pedals to restart.");
            Thread.Sleep(RestartWaitMs);
            PedalConfigReadResult after = null;
            for (int attempt = 1; attempt <= ReadBackAttempts; attempt++)
            {
                after = PedalConfigReader.Read(vendorId, productId, $"after {label}");
                if (after.Success) break;
                report.AppendLine($"  Read back attempt {attempt} failed: {after.Summary}");
                Thread.Sleep(1000);
            }
            if (after == null || !after.Success)
                return Finish(label, false, true, "Sent, but couldn't read the settings back. Read them manually to check.", report);

            int differences = Enumerable.Range(0, settings.Length).Count(i => i >= after.Data.Length || after.Data[i] != settings[i]);
            if (differences == 0)
                return Finish(label, true, true, "Written and verified: the pedals hold exactly what was sent.", report);

            report.AppendLine("Differences (offset: sent -> read back):");
            foreach (int i in Enumerable.Range(0, settings.Length).Where(i => i >= after.Data.Length || after.Data[i] != settings[i]).Take(64))
                report.AppendLine($"  0x{i:X3}: {settings[i]:X2} -> {(i < after.Data.Length ? after.Data[i].ToString("X2") : "missing")}");
            return Finish(label, false, true, $"Sent, but {differences} byte(s) read back differently. See the report file.", report);
        }

        private static string Validate(int vendorId, int productId, byte[] expectedCurrent, byte[] settings)
        {
            if (settings == null || expectedCurrent == null) return "no settings to compare.";
            if (settings.Length != expectedCurrent.Length || settings.Length == 0 || settings.Length % Payload != 0)
                return $"unexpected settings size ({settings?.Length} bytes).";
            // The firmware only accepts its own version; never send a block made for another one.
            if (BitConverter.ToUInt16(settings, 0) != BitConverter.ToUInt16(expectedCurrent, 0))
                return "the firmware version field doesn't match the pedals.";
            if (settings.Length < ProductIdOffset + 2 ||
                BitConverter.ToUInt16(settings, VendorIdOffset) != vendorId ||
                BitConverter.ToUInt16(settings, ProductIdOffset) != productId)
                return "the USB IDs in the settings don't match the pedals; the block doesn't look like Pro X Ultra settings.";
            return null;
        }

        /// <summary>
        /// Runs the exchange. Returns false if it stopped before the last packet (nothing saved), with the reason in
        /// <paramref name="error"/>. Returns true once the last packet is out; <paramref name="error"/> is then set
        /// only if the pedals refused it.
        /// </summary>
        private static bool SendAll(HidDevice device, byte[] settings, int packets, StringBuilder report, out string error)
        {
            error = null;
            if (!device.TryOpen(out HidStream stream))
            {
                error = "couldn't open the settings interface (in use by another program?).";
                return false;
            }

            using (stream)
            {
                stream.ReadTimeout = 100;
                var request = new byte[device.GetMaxOutputReportLength()];
                var reply = new byte[device.GetMaxInputReportLength()];
                if (request.Length < 2 + Payload)
                {
                    error = $"output report too small ({request.Length} bytes).";
                    return false;
                }

                // Packet 0 carries no data; the answer tells us the pedals speak this protocol before we send any.
                int asked = Exchange(stream, request, reply, 0, null);
                report.AppendLine($"  Start request answered with {Describe(asked)}");
                if (asked != 1)
                {
                    error = asked < 0 ? "the pedals didn't answer the write request." : $"unexpected answer to the write request ({Describe(asked)}).";
                    return false;
                }

                for (int packet = 1; packet <= packets; packet++)
                {
                    bool last = packet == packets;
                    int next = Exchange(stream, request, reply, packet, settings, last);
                    if (last)
                    {
                        // Usually no answer at all: the pedals are already saving and restarting.
                        report.AppendLine($"  Packet {packet} (last) answered with {Describe(next)}");
                        if (next == VersionMismatch) error = "The pedals refused the settings (firmware version mismatch). Nothing was saved.";
                        return true;
                    }
                    if (next != packet + 1)
                    {
                        report.AppendLine($"  Packet {packet} answered with {Describe(next)}");
                        error = next == VersionMismatch ? "the pedals refused the settings (firmware version mismatch)."
                            : next < 0 ? $"the pedals stopped answering after packet {packet}."
                            : $"unexpected answer after packet {packet} ({Describe(next)}).";
                        return false;
                    }
                }
                error = "ran out of packets.";
                return false;
            }
        }

        /// <summary>Sends one CONFIG_OUT packet and returns the packet number the pedals ask for next, or -1 for no answer.</summary>
        private static int Exchange(HidStream stream, byte[] request, byte[] reply, int packet, byte[] settings, bool last = false)
        {
            Array.Clear(request, 0, request.Length);
            request[0] = ReportIdConfigOut;
            request[1] = (byte)packet;
            if (packet > 0) Array.Copy(settings, (packet - 1) * Payload, request, 2, Payload);
            stream.Write(request);

            var deadline = DateTime.UtcNow.AddMilliseconds(AckTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                int count;
                try
                {
                    count = stream.Read(reply);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (IOException) when (last)
                {
                    // The pedals restarting drops the interface mid-read.
                    return -1;
                }
                // Joystick and param reports share the pipe; only CONFIG_OUT answers count.
                if (count >= 2 && reply[0] == ReportIdConfigOut) return reply[1];
            }
            return -1;
        }

        private static string Describe(int answer) =>
            answer < 0 ? "no answer" : answer == VersionMismatch ? "[4, 0xFE] (version mismatch)" : $"[4, {answer}]";

        private static PedalConfigWriteResult Finish(string label, bool success, bool sent, string summary, StringBuilder report)
        {
            var result = new PedalConfigWriteResult { Success = success, Sent = sent, Summary = summary };
            try
            {
                Directory.CreateDirectory(PedalConfigReader.OutputFolder);
                report.Insert(0, $"Result: {summary}{Environment.NewLine}");
                result.ReportPath = Path.Combine(PedalConfigReader.OutputFolder,
                    $"pedal-write-{DateTime.Now:yyyyMMdd-HHmmss}{PedalConfigReader.FileLabel(label)}.txt");
                File.WriteAllText(result.ReportPath, report.ToString());
            }
            catch (Exception ex)
            {
                result.Summary += $" (Could not save the report: {ex.Message})";
            }
            SimHub.Logging.Current.Info($"[SimsonnPedals] Settings write ({label}): {result.Summary}");
            return result;
        }
    }
}
