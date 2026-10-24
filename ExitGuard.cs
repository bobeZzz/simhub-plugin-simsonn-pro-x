using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SimsonnPedals
{
    /// <summary>
    /// Starts the embedded exit guard (ExitGuard/Program.cs): a separate process that waits for SimHub to exit and then
    /// sets the vJoy axes to 0. Zeroing from End() or ProcessExit doesn't happen when SimHub crashes or is killed, and on
    /// the rig the brake stayed pressed in iRacing even after a normal exit.
    /// </summary>
    internal static class ExitGuard
    {
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimsonnPedals");

        public static string LogPath => Path.Combine(Folder, "exit-guard.log");

        private static uint startedForDevice;

        /// <summary>Starts a guard for vJoy device <paramref name="deviceId"/>, once per device per SimHub session.</summary>
        public static void Start(uint deviceId)
        {
            if (startedForDevice == deviceId) return;
            try
            {
                string exe = Extract();
                string dll = VJoyOutput.ResolveLibrary();
                int pid = Process.GetCurrentProcess().Id;
                // A guard started for an earlier device id keeps watching too; zeroing that device as well is harmless.
                Process.Start(new ProcessStartInfo(exe, $"{pid} \"{dll}\" {deviceId} \"{LogPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Folder,
                })?.Dispose();
                startedForDevice = deviceId;
                TrimLog();
                SimHub.Logging.Current.Info($"[SimsonnPedals] Exit guard started for vJoy device {deviceId}");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("[SimsonnPedals] Could not start the exit guard", ex);
            }
        }

        /// <summary>Writes the guard exe to %LocalAppData%\SimsonnPedals, named by its hash so a running older copy never blocks it.</summary>
        private static string Extract()
        {
            byte[] bytes;
            using (var stream = typeof(ExitGuard).Assembly.GetManifestResourceStream("SimsonnPedals.ExitGuard.exe"))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                hash = string.Concat(sha.ComputeHash(bytes).Take(4).Select(b => b.ToString("x2")));

            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"SimsonnPedalsExitGuard-{hash}.exe");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) File.WriteAllBytes(path, bytes);

            // Copies from older plugin versions; ones still running are locked and skipped.
            foreach (var old in Directory.GetFiles(Folder, "SimsonnPedalsExitGuard-*.exe").Where(f => f != path))
            {
                try { File.Delete(old); }
                catch (Exception) { /* still running */ }
            }
            return path;
        }

        private static void TrimLog()
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 64 * 1024)
                    File.WriteAllLines(LogPath, File.ReadAllLines(LogPath).Reverse().Take(200).Reverse());
            }
            catch (Exception) { /* best effort */ }
        }
    }
}
