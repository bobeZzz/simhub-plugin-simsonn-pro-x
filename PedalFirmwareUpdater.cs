using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HidSharp;

namespace SimsonnPedals
{
    public sealed class PedalFirmwareUpdateResult
    {
        public bool Success { get; set; }

        /// <summary>True once the bootloader was entered, i.e. the pedals' firmware may have changed.</summary>
        public bool Started { get; set; }

        public string Summary { get; set; }
        public string ReportPath { get; set; }
    }

    /// <summary>
    /// Updates the pedals' firmware the way Simsonn Manager does (research/firmware-update.md):
    /// report 5 "bootloader run" restarts the pedals into the SIMSONN Flasher bootloader, the main image goes to it
    /// with FreeJoy's flash exchange on report 5, then the second chip's image goes through the new main firmware on
    /// report 7, and the versions are read back.
    /// </summary>
    public static class PedalFirmwareUpdater
    {
        private const byte ReportIdBootloader = 5;
        private const byte ReportIdMainFlash = 5;
        private const byte ReportIdSubFlash = 7;
        private const int PacketSize = 64;
        private const int DataPerPacket = 60;
        private const ushort Done = 0xF000, SizeError = 0xF001, CrcError = 0xF002, EraseError = 0xF003;

        private const int FlasherWaitMs = 10000;
        private const int PedalsWaitMs = 20000;
        private const int ReadTimeoutMs = 5000;
        private const int FlashTimeoutMs = 300000;
        private const int OpenAttempts = 5;

        /// <summary>
        /// Flashes <paramref name="firmware"/> (already decrypted and checked). <paramref name="progress"/> gets short
        /// status lines from the worker thread.
        /// </summary>
        public static PedalFirmwareUpdateResult Update(int vendorId, int productId, FirmwareFile firmware, Action<string> progress)
        {
            var report = new StringBuilder();
            report.AppendLine($"SIMSONN pedal firmware update, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"File: {firmware.Path}");
            report.AppendLine($"Main {firmware.MainVersion}: {firmware.MainImage.Length} bytes, CRC {firmware.MainCrc:X4}");
            report.AppendLine($"Second chip {firmware.SubVersion}: {firmware.SubImage.Length} bytes, CRC {firmware.SubCrc:X4}");
            void Step(string line)
            {
                report.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
                progress?.Invoke(line);
            }

            // Checks: nothing has been sent yet, so any failure here leaves the pedals as they are.
            if (firmware.Model != FirmwareFile.ModelForProduct(productId))
                return Finish(false, false, $"Nothing was changed: {firmware.FileName} is for \"{firmware.Model}\", not these pedals.", report);
            if (Process.GetProcessesByName("SIMSONN Manager").Length > 0)
                return Finish(false, false, "Nothing was changed: close Simsonn Manager first, it talks to the bootloader too.", report);
            string versionError;
            // Pedals left in update mode (an earlier update stopped part way): flash both chips again from there.
            HidDevice flasher = FindFlasher(vendorId, report);
            if (flasher != null)
            {
                Step("The pedals are already in update mode; flashing both chips from there.");
            }
            else
            {
                var before = PedalRawMonitor.ReadVersions(vendorId, productId, 3000, out versionError);
                if (before == null)
                    return Finish(false, false, "Nothing was changed: couldn't read the pedals' firmware version: " + versionError, report);
                Step($"Pedals run {PedalRawMonitor.VersionText(before.Value.Main, false)} (second chip {PedalRawMonitor.VersionText(before.Value.Sub, true)}), " +
                     $"version words 0x{before.Value.Main:X4} / 0x{before.Value.Sub:X4}.");

                var settings = PedalConfigReader.FindInterfaces(DeviceList.Local.GetHidDevices(vendorId, productId), ReportIdBootloader, report);
                if (settings.Count != 1)
                    return Finish(false, false, $"Nothing was changed: expected one settings interface, found {settings.Count}.", report);

                // 1. Restart into the bootloader.
                Step("Restarting the pedals into update mode…");
                try
                {
                    SendBootloaderRun(settings[0]);
                }
                catch (Exception ex)
                {
                    return Finish(false, false, $"Nothing was changed: couldn't send the update command ({ex.Message}).", report);
                }

                flasher = WaitFor(() => FindFlasher(vendorId, report: null), FlasherWaitMs);
                if (flasher == null)
                {
                    bool back = DeviceList.Local.GetHidDevices(vendorId, productId).Any();
                    return Finish(false, !back, back
                        ? "Nothing was changed: the pedals didn't switch to update mode."
                        : "The pedals left USB but no SIMSONN Flasher appeared. Replug them; if they don't come back, run the update in Simsonn Manager.", report);
                }
            }
            report.AppendLine();
            report.AppendLine($"Flasher: PID 0x{flasher.ProductID:X4}, \"{SafeName(flasher)}\", {flasher.DevicePath}");
            PedalConfigReader.FindInterfaces(new[] { flasher }, ReportIdMainFlash, report);

            // 2. Main chip.
            string error = FlashWithRetries(flasher, ReportIdMainFlash, firmware.MainImage, firmware.MainCrc, "main firmware", Step);
            if (error != null)
                return Finish(false, true, $"Updating the main firmware failed: {error} The pedals are probably still in update mode: " +
                                           "run the update again here or in Simsonn Manager.", report);

            // 3. Second chip: the flasher itself takes it on report 7 (seen on the rig: the flasher offers reports 5, 7 and 8,
            //    and stays in update mode after the main chip).
            Step("Main firmware written; writing the second chip…");
            Thread.Sleep(1000);
            HidDevice subChannel = WaitFor(() => FindFlasher(vendorId, report: null), FlasherWaitMs);
            if (subChannel == null)
                return Finish(false, true, $"The main firmware is now {firmware.MainVersion}, but the update mode closed before the second chip. " +
                                           "Replug the pedals and run the update again, here or in Simsonn Manager.", report);
            report.AppendLine($"Second chip channel: {subChannel.DevicePath}");
            error = FlashWithRetries(subChannel, ReportIdSubFlash, firmware.SubImage, firmware.SubCrc, "second chip", Step);
            if (error != null)
                return Finish(false, true, $"The main firmware is now {firmware.MainVersion}, but updating the second chip failed: {error} " +
                                           "The pedals stay in update mode: run the update again, here or in Simsonn Manager.", report);

            // 4. The pedals should leave update mode on their own; check what they now report.
            Step("Second chip written; waiting for the pedals to restart…");
            if (WaitFor(() => DeviceList.Local.GetHidDevices(vendorId, productId).FirstOrDefault(), PedalsWaitMs) == null)
                return Finish(false, true, "Both chips were written, but the pedals stayed in update mode. Unplug and replug the USB cable, " +
                                           "then use Check firmware to confirm the versions.", report);
            (ushort Main, ushort Sub)? after = null;
            // Right after the restart the main chip reports the second chip as 0x0000 until it has asked it (seen on the
            // rig with 1.0.10; a read a minute later showed 0x0206), so keep reading until it answers.
            for (int attempt = 0; attempt < 15 && (after == null || after.Value.Sub == 0); attempt++)
            {
                Thread.Sleep(1000);
                after = PedalRawMonitor.ReadVersions(vendorId, productId, 2000, out versionError) ?? after;
            }
            if (after == null)
                return Finish(false, true, "Flashed, but the pedals didn't report their version afterwards. Replug them and use Read pedal settings to check.", report);
            string main = PedalRawMonitor.VersionText(after.Value.Main, false), sub = PedalRawMonitor.VersionText(after.Value.Sub, true);
            report.AppendLine($"Version words now 0x{after.Value.Main:X4} / 0x{after.Value.Sub:X4}");
            bool matches = main == firmware.MainVersion && sub == firmware.SubVersion;
            return Finish(matches, true, matches
                ? $"Firmware updated: main {main}, second chip {sub}."
                : $"Flashed, but the pedals report main {main}, second chip {sub} (expected {firmware.MainVersion} / {firmware.SubVersion}). See the report file.", report);
        }

        /// <summary>True if the pedals are sitting in update mode (the SIMSONN Flasher bootloader).</summary>
        public static bool IsInUpdateMode(int vendorId) => FindFlasher(vendorId, null) != null;

        /// <summary>A connected SIMSONN or FreeJoy Flasher (the pedals' bootloader), or null.</summary>
        private static HidDevice FindFlasher(int vendorId, StringBuilder report)
        {
            foreach (var device in DeviceList.Local.GetHidDevices(vendorId))
            {
                string name = SafeName(device);
                if (name.IndexOf("Flasher", StringComparison.OrdinalIgnoreCase) < 0) continue;
                report?.AppendLine($"Found flasher PID 0x{device.ProductID:X4} \"{name}\"");
                return device;
            }
            return null;
        }

        private static string SafeName(HidDevice device)
        {
            try
            {
                return device.GetProductName() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static HidDevice WaitFor(Func<HidDevice> find, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var device = find();
                if (device != null) return device;
                Thread.Sleep(200);
            }
            return null;
        }

        private static void SendBootloaderRun(HidDevice device)
        {
            using (var stream = device.Open())
            {
                var packet = new byte[device.GetMaxOutputReportLength()];
                packet[0] = ReportIdBootloader;
                Encoding.ASCII.GetBytes("bootloader run").CopyTo(packet, 1);
                Send(stream, packet);
            }
        }

        /// <summary>Opens <paramref name="device"/> (a fresh enumeration can take a moment) and flashes; null on success.</summary>
        private static string FlashWithRetries(HidDevice device, byte reportId, byte[] image, ushort crc, string what, Action<string> step)
        {
            HidStream stream = null;
            for (int attempt = 1; attempt <= OpenAttempts && stream == null; attempt++)
            {
                if (!device.TryOpen(out stream)) stream = null;
                if (stream == null) Thread.Sleep(500);
            }
            if (stream == null) return $"couldn't open the {what} channel.";
            using (stream)
            {
                try
                {
                    return Flash(stream, reportId, image, crc, what, step);
                }
                catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is ObjectDisposedException)
                {
                    return $"the connection dropped ({ex.Message}).";
                }
            }
        }

        /// <summary>
        /// FreeJoy's flash exchange: a start packet with length and CRC, then the device asks for packet n
        /// (big-endian in bytes 1–2) and gets 60 bytes of image; 0xF000 means done, 0xF001–3 an error.
        /// </summary>
        private static string Flash(HidStream stream, byte reportId, byte[] image, ushort crc, string what, Action<string> step)
        {
            stream.ReadTimeout = ReadTimeoutMs;
            if (stream.Device.GetMaxOutputReportLength() < PacketSize)
                return $"the {what} channel takes only {stream.Device.GetMaxOutputReportLength()}-byte packets.";
            var reply = new byte[Math.Max(PacketSize, stream.Device.GetMaxInputReportLength())];
            return Exchange(() =>
            {
                try
                {
                    int count = stream.Read(reply);
                    return reply.Take(count).ToArray();
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }, packet => Send(stream, packet), stream.Device.GetMaxOutputReportLength(), reportId, image, crc, what, step);
        }

        /// <summary>
        /// The exchange itself, on plain read/write callbacks so it can be tested against a simulated bootloader.
        /// <paramref name="read"/> returns one input report, or null after <see cref="ReadTimeoutMs"/> without one.
        /// </summary>
        internal static string Exchange(Func<byte[]> read, Action<byte[]> write, int packetLength, byte reportId, byte[] image, ushort crc,
            string what, Action<string> step)
        {
            int packets = (image.Length + DataPerPacket - 1) / DataPerPacket;
            var packet = new byte[packetLength];

            packet[0] = reportId;
            packet[4] = (byte)image.Length;
            packet[5] = (byte)(image.Length >> 8);
            packet[6] = (byte)crc;
            packet[7] = (byte)(crc >> 8);
            write(packet);
            step?.Invoke($"Erasing and writing the {what}…");

            var deadline = DateTime.UtcNow.AddMilliseconds(FlashTimeoutMs);
            int lastPercent = -1;
            while (DateTime.UtcNow < deadline)
            {
                byte[] reply = read();
                if (reply == null) return $"no answer for {ReadTimeoutMs / 1000} s.";
                // Other reports may share the interface; only flash answers count.
                if (reply.Length < 3 || (reply[0] != 4 && reply[0] != 5 && reply[0] != 7)) continue;
                int n = reply[1] << 8 | reply[2];
                switch (n)
                {
                    case Done: return null;
                    case SizeError: return "the bootloader rejected the size (SIZE ERROR).";
                    case CrcError: return "the bootloader's checksum didn't match (CRC ERROR).";
                    case EraseError: return "the bootloader couldn't erase its flash (ERASE ERROR).";
                }
                if ((n & 0xF000) == 0xF000) return $"unknown bootloader status 0x{n:X4}.";
                if (n < 1 || n > packets) return $"the bootloader asked for packet {n} of {packets}.";

                Array.Clear(packet, 0, packet.Length);
                packet[0] = reportId;
                packet[1] = (byte)(n >> 8);
                packet[2] = (byte)n;
                int offset = (n - 1) * DataPerPacket;
                Buffer.BlockCopy(image, offset, packet, 4, Math.Min(DataPerPacket, image.Length - offset));
                write(packet);

                int percent = offset * 100 / image.Length;
                if (percent / 5 != lastPercent / 5)
                {
                    lastPercent = percent;
                    step?.Invoke($"Writing the {what}: {percent} %");
                }
            }
            return "took longer than 5 minutes.";
        }

        // The only write path: bootloader entry and flash packets, nothing else.
        private static void Send(HidStream stream, byte[] packet)
        {
            if (packet[0] != ReportIdBootloader && packet[0] != ReportIdSubFlash)
                throw new InvalidOperationException("Only firmware update packets may be sent here.");
            stream.Write(packet);
        }

        private static PedalFirmwareUpdateResult Finish(bool success, bool started, string summary, StringBuilder report)
        {
            var result = new PedalFirmwareUpdateResult { Success = success, Started = started, Summary = summary };
            try
            {
                Directory.CreateDirectory(PedalConfigReader.OutputFolder);
                report.Insert(0, $"Result: {summary}{Environment.NewLine}");
                result.ReportPath = Path.Combine(PedalConfigReader.OutputFolder, $"pedal-firmware-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(result.ReportPath, report.ToString());
            }
            catch (Exception ex)
            {
                result.Summary += $" (Could not save the report: {ex.Message})";
            }
            SimHub.Logging.Current.Info($"[SimsonnPedals] Firmware update: {result.Summary}");
            return result;
        }
    }
}
