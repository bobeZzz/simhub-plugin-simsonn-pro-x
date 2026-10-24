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
    /// through the settings block. Only two commands may be sent: <c>brakeCutoff</c> (the firmware's throttle cut; vJoy
    /// mode keeps it off, since it would zero the throttle behind the plugin's back) and <c>damping_max</c> (the brake's
    /// max force: raw max and output high end, which the firmware saves and reloads live, see research/direct-mode.md).
    /// </summary>
    public static class PedalCommandWriter
    {
        private const byte ReportIdCommand = 6;
        private const string BrakeCutoff = "brakeCutoff";
        private const string DampingMax = "damping_max";

        /// <summary>The firmware caps the throttle cut threshold at 50 % (500 tenths).</summary>
        public const int MaxBrakeCutoffTenths = 500;

        /// <summary>Sets the firmware throttle cut and reads it back. Returns the state read back, or null.</summary>
        public static PedalStatusReader.BrakeCutoff? SetBrakeCutoff(int vendorId, int productId, bool enabled, int thresholdTenths, StringBuilder report)
        {
            if (thresholdTenths < 1 || thresholdTenths > MaxBrakeCutoffTenths)
                throw new ArgumentOutOfRangeException(nameof(thresholdTenths), "The firmware throttle cut works from 0.1 to 50 % brake.");
            return WithCommandStream(vendorId, productId, report, (stream, request, reply) =>
            {
                report.AppendLine($"  {BrakeCutoff} (enabled {(enabled ? 1 : 0)}, threshold {thresholdTenths}):");
                Prepare(request, BrakeCutoff);
                int payload = 1 + BrakeCutoff.Length;
                request[payload] = (byte)(enabled ? 1 : 0);
                request[payload + 1] = (byte)(thresholdTenths & 0xFF);
                request[payload + 2] = (byte)(thresholdTenths >> 8);
                Send(stream, request);
                PedalStatusReader.WaitForCommandReply(stream, reply, report);
                return PedalStatusReader.ReadBrakeCutoff(stream, request, reply, report);
            });
        }

        /// <summary>
        /// Sends <c>damping_max</c>: the brake's raw max (+8) and output high end (+4); the firmware also sets the centre to
        /// the middle of the output ends. Returns false if the settings interface couldn't be used.
        /// </summary>
        public static bool SetDampingMax(int vendorId, int productId, int rawMax, int outHigh, StringBuilder report)
        {
            if (rawMax < short.MinValue || rawMax > short.MaxValue || outHigh < short.MinValue || outHigh > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(rawMax), "Brake values must fit in 16 bits.");
            return WithCommandStream<bool>(vendorId, productId, report, (stream, request, reply) =>
            {
                report.AppendLine($"  {DampingMax} (raw max {rawMax}, output high end {outHigh}):");
                Prepare(request, DampingMax);
                int payload = 1 + DampingMax.Length;
                BitConverter.GetBytes((short)rawMax).CopyTo(request, payload);
                BitConverter.GetBytes((short)outHigh).CopyTo(request, payload + 2);
                Send(stream, request);
                // 2.1.1 answers "DMOK"; 2.0.8 sends nothing back.
                PedalStatusReader.WaitForCommandReply(stream, reply, report);
                return true;
            }) ?? false;
        }

        private static T? WithCommandStream<T>(int vendorId, int productId, StringBuilder report, Func<HidStream, byte[], byte[], T?> action) where T : struct
        {
            var devices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
            var device = PedalConfigReader.FindInterfaces(devices, ReportIdCommand, report).FirstOrDefault();
            if (device == null || !device.TryOpen(out HidStream stream))
            {
                report.AppendLine("  The settings interface wasn't found or couldn't be opened.");
                return null;
            }
            using (stream)
            {
                stream.ReadTimeout = 100;
                return action(stream, new byte[device.GetMaxOutputReportLength()], new byte[device.GetMaxInputReportLength()]);
            }
        }

        private static void Prepare(byte[] request, string command)
        {
            Array.Clear(request, 0, request.Length);
            request[0] = ReportIdCommand;
            Encoding.ASCII.GetBytes(command, 0, command.Length, request, 1);
        }

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
                Prepare(request, BrakeCutoff);
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

        /// <summary>
        /// The single write path: only "brakeCutoff" (enabled 0/1, threshold 1–500) or "damping_max" (two int16) may be
        /// sent, with nothing after their payload.
        /// </summary>
        private static void Send(HidStream stream, byte[] request)
        {
            bool Is(string command) => request[0] == ReportIdCommand &&
                                       Encoding.ASCII.GetString(request, 1, command.Length) == command;
            bool allowed;
            if (Is(BrakeCutoff))
            {
                int payload = 1 + BrakeCutoff.Length;
                int threshold = BitConverter.ToUInt16(request, payload + 1);
                allowed = request[payload] <= 1 && threshold >= 1 && threshold <= MaxBrakeCutoffTenths &&
                          request.Skip(payload + 3).All(b => b == 0);
            }
            else if (Is(DampingMax))
                allowed = request.Skip(1 + DampingMax.Length + 4).All(b => b == 0);
            else
                allowed = false;
            if (!allowed) throw new InvalidOperationException("Only the firmware throttle cut and brake max force may be sent.");
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
