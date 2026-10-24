using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HidSharp;
using HidSharp.Reports;

namespace SimsonnPedals
{
    public sealed class PedalConfigReadResult
    {
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string BinaryPath { get; set; }
        public string ReportPath { get; set; }

        /// <summary>The settings block as read, or null if the read failed.</summary>
        public byte[] Data { get; set; }

        /// <summary>Short summary of the live status query (<see cref="PedalStatusReader"/>), or null if not asked or nothing read.</summary>
        public string LiveSummary { get; set; }
    }

    /// <summary>
    /// Read-only download of the settings stored in the pedals. Simsonn's firmware is derived from FreeJoy, which
    /// returns its settings block in 62-byte packets when the host sends a CONFIG_IN request naming the packet.
    /// This class only ever sends that request; writing is <see cref="PedalConfigWriter"/>'s job.
    /// </summary>
    public static class PedalConfigReader
    {
        // FreeJoy report IDs: 1 joystick, 2 params, 3 config in, 4 config out, 5 firmware. Only 3 is sent here.
        private const byte ReportIdConfigIn = 3;
        internal const int PayloadPerPacket = 62;
        private const int MaxPackets = 64;
        private const int ReplyTimeoutMs = 600;
        private const int FirstRequestAttempts = 4;
        private const int FirstRequestRetryDelayMs = 1000;

        public static string OutputFolder =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PluginsData", "SimsonnPedals");

        /// <param name="label">Optional note (e.g. "brake deadzone 10") added to the file names and report.</param>
        /// <param name="includeLiveStatus">Also query what the pedals keep outside the block (<see cref="PedalStatusReader"/>); takes ~6 s more.</param>
        public static PedalConfigReadResult Read(int vendorId, int productId, string label = null, bool includeLiveStatus = false)
        {
            var report = new StringBuilder();
            report.AppendLine($"SIMSONN pedal settings dump, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(label)) report.AppendLine($"Label: {label.Trim()}");
            report.AppendLine($"Looking for VID_{vendorId:X4} PID_{productId:X4}");
            report.AppendLine();

            var devices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
            if (devices.Count == 0)
                return Finish(label, false, "Pedals not found on USB.", report, null);

            var candidates = FindInterfaces(devices, ReportIdConfigIn, report);
            if (candidates.Count == 0)
                return Finish(label, false, "No interface offers FreeJoy-style settings reports (ID 3). See the report file.", report, null);

            foreach (var device in candidates)
            {
                report.AppendLine($"Reading settings from {device.DevicePath}");
                byte[] data;
                int packets;
                try
                {
                    data = TryReadConfig(device, report, out packets);
                }
                catch (Exception ex)
                {
                    report.AppendLine($"  Failed: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (data == null) continue;

                report.AppendLine($"  Received {packets} packet(s), {data.Length} bytes (the last packet may be padded).");
                report.AppendLine();
                AppendAnalysis(report, data);
                report.AppendLine();
                string live = null;
                if (includeLiveStatus)
                {
                    try
                    {
                        live = PedalStatusReader.Query(device, report);
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"  Live status failed: {ex.GetType().Name}: {ex.Message}");
                        report.AppendLine();
                    }
                }
                report.AppendLine("Hex dump:");
                AppendHexDump(report, data);
                var result = Finish(label, true, $"Read {data.Length} bytes in {packets} packets.", report, data);
                result.LiveSummary = live;
                return result;
            }

            return Finish(label, false, "The pedals did not answer the settings request. See the report file.", report, null);
        }

        private static byte[] TryReadConfig(HidDevice device, StringBuilder report, out int packets)
        {
            packets = 0;
            if (!device.TryOpen(out HidStream stream))
            {
                report.AppendLine("  Could not open the interface (in use by another program?).");
                return null;
            }

            using (stream)
            {
                stream.ReadTimeout = 100;
                var data = new List<byte>();
                var request = new byte[device.GetMaxOutputReportLength()];
                var reply = new byte[device.GetMaxInputReportLength()];
                if (request.Length < 2 || reply.Length < 2 + PayloadPerPacket)
                {
                    report.AppendLine($"  Report sizes too small for FreeJoy packets (out {request.Length}, in {reply.Length}).");
                    return null;
                }

                for (int packet = 1; packet <= MaxPackets; packet++)
                {
                    Array.Clear(request, 0, request.Length);
                    request[0] = ReportIdConfigIn;
                    request[1] = (byte)packet;
                    SendConfigInRequest(stream, request);
                    bool answered = WaitForPacket(stream, reply, packet);

                    // Right after Simsonn Manager saves, the pedals restart and can miss the first request; ask again.
                    for (int attempt = 1; !answered && packet == 1 && attempt < FirstRequestAttempts; attempt++)
                    {
                        report.AppendLine($"  No reply to the first settings request, asking again ({attempt + 1}/{FirstRequestAttempts}).");
                        System.Threading.Thread.Sleep(FirstRequestRetryDelayMs);
                        SendConfigInRequest(stream, request);
                        answered = WaitForPacket(stream, reply, packet);
                    }

                    if (!answered)
                    {
                        // FreeJoy ignores requests past the last packet, so silence after packet 1 means "done".
                        if (packet == 1)
                        {
                            report.AppendLine("  No reply to the first settings request.");
                            return null;
                        }
                        break;
                    }

                    data.AddRange(reply.Skip(2).Take(PayloadPerPacket));
                    packets = packet;
                }
                return data.ToArray();
            }
        }

        /// <summary>Interfaces that both accept and send reports with <paramref name="reportId"/>, described into <paramref name="report"/>.</summary>
        internal static List<HidDevice> FindInterfaces(IEnumerable<HidDevice> devices, byte reportId, StringBuilder report)
        {
            var found = new List<HidDevice>();
            foreach (var device in devices)
            {
                report.AppendLine($"Interface: {device.DevicePath}");
                report.AppendLine($"  Max report lengths: in {device.GetMaxInputReportLength()}, out {device.GetMaxOutputReportLength()}, feature {device.GetMaxFeatureReportLength()}");
                try
                {
                    var descriptor = device.GetReportDescriptor();
                    report.AppendLine($"  Input reports:   {Describe(descriptor.InputReports)}");
                    report.AppendLine($"  Output reports:  {Describe(descriptor.OutputReports)}");
                    report.AppendLine($"  Feature reports: {Describe(descriptor.FeatureReports)}");
                    if (descriptor.TryGetReport(ReportType.Output, reportId, out _) &&
                        descriptor.TryGetReport(ReportType.Input, reportId, out _))
                        found.Add(device);
                }
                catch (Exception ex)
                {
                    report.AppendLine($"  Could not read report descriptor: {ex.Message}");
                }
            }
            report.AppendLine();
            return found;
        }

        /// <summary>The single write path in this class; it refuses anything but a CONFIG_IN request.</summary>
        private static void SendConfigInRequest(HidStream stream, byte[] request)
        {
            if (request[0] != ReportIdConfigIn)
                throw new InvalidOperationException("Only settings read requests may be sent to the pedals.");
            stream.Write(request);
        }

        private static bool WaitForPacket(HidStream stream, byte[] reply, int packet)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ReplyTimeoutMs);
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
                // Joystick and param reports share the pipe; skip anything that isn't our reply.
                if (count >= 2 + PayloadPerPacket && reply[0] == ReportIdConfigIn && reply[1] == packet)
                    return true;
            }
            return false;
        }

        private static void AppendAnalysis(StringBuilder report, byte[] data)
        {
            if (data.Length >= 2)
            {
                ushort version = BitConverter.ToUInt16(data, 0);
                report.AppendLine($"First 16-bit value (FreeJoy stores firmware_version here): 0x{version:X4} = {version}");
            }

            report.AppendLine("Text found in the settings:");
            foreach (var (offset, text) in FindStrings(data, 4))
                report.AppendLine($"  @0x{offset:X4} ({offset}): \"{text}\"");
        }

        private static IEnumerable<(int Offset, string Text)> FindStrings(byte[] data, int minLength)
        {
            int start = -1;
            for (int i = 0; i <= data.Length; i++)
            {
                bool printable = i < data.Length && data[i] >= 0x20 && data[i] < 0x7F;
                if (printable && start < 0) start = i;
                if (!printable && start >= 0)
                {
                    if (i - start >= minLength) yield return (start, Encoding.ASCII.GetString(data, start, i - start));
                    start = -1;
                }
            }
        }

        internal static void AppendHexDump(StringBuilder report, byte[] data)
        {
            for (int row = 0; row < data.Length; row += 16)
            {
                int len = Math.Min(16, data.Length - row);
                var hex = string.Join(" ", data.Skip(row).Take(len).Select(b => b.ToString("X2")));
                var ascii = new string(data.Skip(row).Take(len).Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());
                report.AppendLine($"{row:X4}  {hex,-47}  {ascii}");
            }
        }

        internal static string FileLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(label.Trim().Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch).ToArray());
            return "-" + (clean.Length > 40 ? clean.Substring(0, 40) : clean);
        }

        private static string Describe(IEnumerable<Report> reports)
        {
            var list = reports.Select(r => $"ID {r.ReportID} ({r.Length} bytes)").ToList();
            return list.Count == 0 ? "none" : string.Join(", ", list);
        }

        private static PedalConfigReadResult Finish(string label, bool success, string summary, StringBuilder report, byte[] data)
        {
            var result = new PedalConfigReadResult { Success = success, Summary = summary, Data = data };
            try
            {
                Directory.CreateDirectory(OutputFolder);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + FileLabel(label);
                report.Insert(0, $"Result: {summary}{Environment.NewLine}");
                result.ReportPath = Path.Combine(OutputFolder, $"pedal-settings-{stamp}.txt");
                File.WriteAllText(result.ReportPath, report.ToString());
                if (data != null)
                {
                    result.BinaryPath = Path.Combine(OutputFolder, $"pedal-settings-{stamp}.bin");
                    File.WriteAllBytes(result.BinaryPath, data);
                }
            }
            catch (Exception ex)
            {
                result.Summary += $" (Could not save files: {ex.Message})";
            }
            SimHub.Logging.Current.Info($"[SimsonnPedals] Settings read: {result.Summary}");
            return result;
        }
    }
}
