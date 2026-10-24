using System;
using System.IO;
using System.Linq;
using System.Text;
using HidSharp;

namespace SimsonnPedals
{
    public sealed class PedalCommandResult
    {
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string ReportPath { get; set; }
    }

    /// <summary>
    /// Live settings changes over the pedals' report 6 command channel (research/manager-protocol.md). These don't go
    /// through the settings block, so the pedals don't restart. The only change allowed here is switching the firmware
    /// throttle cut off: the plugin does throttle cut itself, and the firmware's would zero the throttle behind its back.
    /// </summary>
    public static class PedalCommandWriter
    {
        private const byte ReportIdCommand = 6;
        private const string BrakeCutoff = "brakeCutoff";

        /// <summary>Switches the firmware throttle cut off (the threshold is kept) and reads it back.</summary>
        public static PedalCommandResult SwitchThrottleCutOff(int vendorId, int productId)
        {
            var report = new StringBuilder();
            report.AppendLine($"SIMSONN pedal command, {DateTime.Now:yyyy-MM-dd HH:mm:ss}: switch the firmware throttle cut off");
            report.AppendLine();

            var devices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
            var device = PedalConfigReader.FindInterfaces(devices, ReportIdCommand, report).FirstOrDefault();
            if (device == null || !device.TryOpen(out HidStream stream))
                return Finish(false, "The pedals' settings interface wasn't found or couldn't be opened.", report);

            using (stream)
            {
                stream.ReadTimeout = 100;
                var request = new byte[device.GetMaxOutputReportLength()];
                var reply = new byte[device.GetMaxInputReportLength()];

                var before = PedalStatusReader.ReadBrakeCutoff(stream, request, reply, report);
                if (before == null)
                    return Finish(false, "The pedals didn't report their throttle cut; nothing was changed.", report);
                if (!before.Value.Enabled)
                    return Finish(true, before.Value.ToString(), report);

                report.AppendLine($"  {BrakeCutoff} (enabled 0, threshold {before.Value.ThresholdTenths}):");
                Array.Clear(request, 0, request.Length);
                request[0] = ReportIdCommand;
                Encoding.ASCII.GetBytes(BrakeCutoff, 0, BrakeCutoff.Length, request, 1);
                int payload = 1 + BrakeCutoff.Length;
                request[payload] = 0;
                request[payload + 1] = (byte)(before.Value.ThresholdTenths & 0xFF);
                request[payload + 2] = (byte)(before.Value.ThresholdTenths >> 8);
                Send(stream, request);
                PedalStatusReader.WaitForCommandReply(stream, reply, report);

                var after = PedalStatusReader.ReadBrakeCutoff(stream, request, reply, report);
                if (after == null)
                    return Finish(false, "Sent the switch-off, but the pedals didn't report their throttle cut afterwards.", report);
                return after.Value.Enabled
                    ? Finish(false, "The pedals kept their throttle cut on: " + after.Value, report)
                    : Finish(true, after.Value.ToString(), report);
            }
        }

        /// <summary>The single write path: only "brakeCutoff" with enabled = 0 may be sent.</summary>
        private static void Send(HidStream stream, byte[] request)
        {
            int payload = 1 + BrakeCutoff.Length;
            bool allowed = request[0] == ReportIdCommand &&
                           Encoding.ASCII.GetString(request, 1, BrakeCutoff.Length) == BrakeCutoff &&
                           request[payload] == 0 &&
                           request.Skip(payload + 3).All(b => b == 0);
            if (!allowed) throw new InvalidOperationException("Only switching the firmware throttle cut off may be sent.");
            stream.Write(request);
        }

        private static PedalCommandResult Finish(bool success, string summary, StringBuilder report)
        {
            var result = new PedalCommandResult { Success = success, Summary = summary };
            try
            {
                Directory.CreateDirectory(PedalConfigReader.OutputFolder);
                report.Insert(0, $"Result: {summary}{Environment.NewLine}");
                result.ReportPath = Path.Combine(PedalConfigReader.OutputFolder,
                    $"pedal-command-{DateTime.Now:yyyyMMdd-HHmmss}-throttle-cut-off.txt");
                File.WriteAllText(result.ReportPath, report.ToString());
            }
            catch (Exception ex)
            {
                result.Summary += $" (Could not save the report: {ex.Message})";
            }
            SimHub.Logging.Current.Info($"[SimsonnPedals] Throttle cut off: {result.Summary}");
            return result;
        }
    }
}
