using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HidSharp;
using HidSharp.Reports;

namespace SimsonnPedals
{
    /// <summary>
    /// Read-only queries of what the pedals keep outside the settings block, as Simsonn Manager does it
    /// (research/manager-protocol.md): report 6 read commands (firmware throttle cut, elastomer, raw sensors) and the
    /// FreeJoy params report (ID 2), which streams raw sensor values after the host asks for it.
    /// Nothing here changes a setting; the send path refuses anything but the requests listed below.
    /// </summary>
    public static class PedalStatusReader
    {
        private const byte ReportIdParams = 2;
        private const byte ReportIdCommand = 6;
        private const int CommandReplyTimeoutMs = 800;
        private const int ParamsCaptureMs = 3000;
        // Simsonn Manager repeats its params request every 5 s; ask a bit more often while capturing.
        private const int ParamsRequestEveryMs = 1000;
        private const int Channels = 6;

        private static readonly string[] ReadCommands = { "readBrakeCutoff", "read_damping_type", "read_raw" };

        /// <summary>
        /// Queries the pedals on <paramref name="device"/> (the settings interface) and writes everything to
        /// <paramref name="report"/>. Returns a short summary for the UI, or null if nothing could be read.
        /// </summary>
        public static string Query(HidDevice device, StringBuilder report)
        {
            report.AppendLine("Live status (report 6 read commands and params report 2; research/manager-protocol.md)");
            ReportDescriptor descriptor;
            try
            {
                descriptor = device.GetReportDescriptor();
            }
            catch (Exception ex)
            {
                report.AppendLine($"  Could not read report descriptor: {ex.Message}");
                return null;
            }
            bool hasCommands = descriptor.TryGetReport(ReportType.Output, ReportIdCommand, out _);
            bool hasParams = descriptor.TryGetReport(ReportType.Output, ReportIdParams, out _);
            if (!hasCommands && !hasParams)
            {
                report.AppendLine("  The interface offers neither output report 6 nor 2.");
                return null;
            }

            if (!device.TryOpen(out HidStream stream))
            {
                report.AppendLine("  Could not open the interface (in use by another program?).");
                return null;
            }

            var summary = new List<string>();
            using (stream)
            {
                stream.ReadTimeout = 100;
                var request = new byte[device.GetMaxOutputReportLength()];
                var reply = new byte[device.GetMaxInputReportLength()];

                if (hasCommands)
                {
                    foreach (string command in ReadCommands)
                    {
                        string line = QueryCommand(stream, request, reply, command, report);
                        if (line != null) summary.Add(line);
                    }
                }
                else report.AppendLine("  No output report 6: command reads skipped.");

                if (hasParams)
                {
                    string line = CaptureParams(stream, request, reply, report);
                    if (line != null) summary.Add(line);
                }
                else report.AppendLine("  No output report 2: params capture skipped.");
            }
            report.AppendLine();
            return summary.Count == 0 ? null : string.Join(Environment.NewLine, summary);
        }

        private static string QueryCommand(HidStream stream, byte[] request, byte[] reply, string command, StringBuilder report)
        {
            report.AppendLine($"  {command}:");
            Array.Clear(request, 0, request.Length);
            request[0] = ReportIdCommand;
            Encoding.ASCII.GetBytes(command, 0, command.Length, request, 1);
            Send(stream, request);

            var deadline = DateTime.UtcNow.AddMilliseconds(CommandReplyTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                int count = TryRead(stream, reply);
                if (count < 2 || reply[0] != ReportIdCommand) continue;
                report.AppendLine($"    reply: {Hex(reply, Math.Min(count, 16))}  \"{Ascii(reply, 1, Math.Min(count, 16) - 1)}\"");
                string parsed = ParseReply(command, reply, count);
                report.AppendLine($"    {parsed ?? "not in the expected format"}");
                return parsed;
            }
            report.AppendLine("    no reply");
            return null;
        }

        private static string ParseReply(string command, byte[] r, int count)
        {
            bool Starts(string text) => count > text.Length && Ascii(r, 1, text.Length) == text;
            switch (command)
            {
                case "readBrakeCutoff" when Starts("BCOK") && count >= 8:
                    int threshold = BitConverter.ToUInt16(r, 6);
                    return r[5] == 1
                        ? $"Firmware throttle cut: on, above {threshold / 10.0:0.#} % brake"
                        : $"Firmware throttle cut: off (threshold {threshold / 10.0:0.#} %)";
                case "read_damping_type" when Starts("OK") && count >= 4:
                    string name = r[3] == 's' ? "soft" : r[3] == 'm' ? "medium" : r[3] == 'h' ? "hard" : $"'{(char)r[3]}' (0x{r[3]:X2})";
                    return $"Elastomer (stored in the firmware): {name}";
                case "read_raw" when Starts("OK") && count >= 9:
                    var values = Enumerable.Range(0, 3).Select(i => BitConverter.ToInt16(r, 3 + 2 * i));
                    return "read_raw: " + string.Join(", ", values);
                default:
                    return null;
            }
        }

        /// <summary>Asks for params reports for a few seconds and logs min/max of each raw and processed channel.</summary>
        private static string CaptureParams(HidStream stream, byte[] request, byte[] reply, StringBuilder report)
        {
            report.AppendLine($"  params (report 2), {ParamsCaptureMs / 1000} s:");
            var rawMin = Enumerable.Repeat(int.MaxValue, Channels).ToArray();
            var rawMax = Enumerable.Repeat(int.MinValue, Channels).ToArray();
            var outMin = (int[])rawMin.Clone();
            var outMax = (int[])rawMax.Clone();
            int packets = 0, firstPackets = 0;

            var end = DateTime.UtcNow.AddMilliseconds(ParamsCaptureMs);
            var nextRequest = DateTime.UtcNow;
            while (DateTime.UtcNow < end)
            {
                if (DateTime.UtcNow >= nextRequest)
                {
                    Array.Clear(request, 0, request.Length);
                    request[0] = ReportIdParams;
                    Send(stream, request);
                    nextRequest = DateTime.UtcNow.AddMilliseconds(ParamsRequestEveryMs);
                }

                int count = TryRead(stream, reply);
                if (count < 2 || reply[0] != ReportIdParams) continue;
                packets++;
                if (packets <= 4) report.AppendLine($"    packet {reply[1]}: {Hex(reply, count)}");
                // Packet 0 holds the struct's first 62 bytes: raw channel i at struct offset 4 + 2i, processed at 20 + 2i.
                if (reply[1] != 0 || count < 2 + 20 + 2 * Channels) continue;
                firstPackets++;
                for (int i = 0; i < Channels; i++)
                {
                    int raw = BitConverter.ToInt16(reply, 2 + 4 + 2 * i);
                    int processed = BitConverter.ToInt16(reply, 2 + 20 + 2 * i);
                    rawMin[i] = Math.Min(rawMin[i], raw);
                    rawMax[i] = Math.Max(rawMax[i], raw);
                    outMin[i] = Math.Min(outMin[i], processed);
                    outMax[i] = Math.Max(outMax[i], processed);
                }
            }

            report.AppendLine($"    {packets} params packet(s), {firstPackets} of them packet 0");
            if (firstPackets == 0) return null;
            string[] names = { "throttle", "brake", "clutch", "throttle 2nd", "brake 2nd", "clutch 2nd" };
            for (int i = 0; i < Channels; i++)
                report.AppendLine($"    channel {i} ({names[i]}): raw {rawMin[i]}…{rawMax[i]}, processed {outMin[i]}…{outMax[i]}");
            return $"Raw sensors: throttle {rawMin[0]}…{rawMax[0]}, brake {rawMin[1]}…{rawMax[1]} ({firstPackets} samples)";
        }

        /// <summary>The single write path: only a params request or one of <see cref="ReadCommands"/> may be sent.</summary>
        private static void Send(HidStream stream, byte[] request)
        {
            bool allowed;
            if (request[0] == ReportIdParams)
                allowed = request.Skip(1).All(b => b == 0);
            else if (request[0] == ReportIdCommand)
            {
                string command = ReadCommands.FirstOrDefault(c => Ascii(request, 1, c.Length) == c);
                allowed = command != null && request.Skip(1 + command.Length).All(b => b == 0);
            }
            else allowed = false;
            if (!allowed) throw new InvalidOperationException("Only read requests may be sent to the pedals.");
            stream.Write(request);
        }

        private static int TryRead(HidStream stream, byte[] reply)
        {
            try
            {
                return stream.Read(reply);
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        private static string Hex(byte[] data, int count) => string.Join(" ", data.Take(count).Select(b => b.ToString("X2")));

        private static string Ascii(byte[] data, int start, int count) =>
            new string(data.Skip(start).Take(count).Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());
    }
}
