using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimsonnPedals.ExitGuard
{
    /// <summary>
    /// Started by the plugin; waits for SimHub to exit and then sets every axis of the plugin's vJoy device to 0.
    /// vJoy keeps an axis wherever its feeder left it, so without this a SimHub crash or kill while braking leaves
    /// the brake pressed in the game. Runs after any kind of exit, including Task Manager.
    /// Usage: SimsonnPedalsExitGuard.exe pid vJoyInterface.dll-path device-id log-path
    /// </summary>
    internal static class Program
    {
        // Axes vJoy can have, as HID usages: X, Y, Z, Rx, Ry, Rz, slider 0, slider 1.
        private static readonly uint[] Axes = { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37 };
        private const int AcquireTimeoutMs = 10000;

        private static string logPath;

        private static int Main(string[] args)
        {
            if (args.Length < 4 || !int.TryParse(args[0], out int pid) || !uint.TryParse(args[2], out uint id)) return 2;
            logPath = args[3];

            // One guard per SimHub process and vJoy device, however often the plugin starts.
            using (var mutex = new Mutex(true, $@"Local\SimsonnPedalsExitGuard-{pid}-{id}", out bool first))
            {
                if (!first) return 0;
                Process simHub;
                try { simHub = Process.GetProcessById(pid); }
                catch (ArgumentException) { return 0; }

                Log($"Watching SimHub (process {pid}), vJoy device {id}");
                simHub.WaitForExit();
                return ZeroDevice(args[1], id) ? 0 : 1;
            }
        }

        private static bool ZeroDevice(string dllPath, uint id)
        {
            if (LoadLibrary(dllPath) == IntPtr.Zero)
            {
                Log($"SimHub exited; could not load {dllPath} (error {Marshal.GetLastWin32Error()})");
                return false;
            }
            try
            {
                // The driver frees the device when SimHub's handle closes; give it a moment.
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    int status = Native.GetVJDStatus(id);
                    if (status == 1 /* free */ && Native.AcquireVJD(id)) break;
                    if (status == 2 /* busy */ && clock.ElapsedMilliseconds > 2000)
                    {
                        // Someone else feeds it now (SimHub started again): their values, not ours.
                        Log("SimHub exited; vJoy device is in use by another program, left alone");
                        return true;
                    }
                    if (clock.ElapsedMilliseconds > AcquireTimeoutMs)
                    {
                        Log($"SimHub exited; could not acquire vJoy device {id} (status {status})");
                        return false;
                    }
                    Thread.Sleep(100);
                }

                int zeroed = 0;
                foreach (uint axis in Axes)
                {
                    if (!Native.GetVJDAxisExist(id, axis)) continue;
                    int min = 0;
                    try { Native.GetVJDAxisMin(id, axis, ref min); }
                    catch (EntryPointNotFoundException) { /* older vJoy builds: axes start at 0 */ }
                    if (Native.SetAxis(min, id, axis)) zeroed++;
                }
                Native.RelinquishVJD(id);
                Log($"SimHub exited; set {zeroed} vJoy axes to 0");
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                Log("SimHub exited; vJoy call failed: " + ex.Message);
                return false;
            }
        }

        private static void Log(string message)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
            catch (Exception) { /* logging is best effort */ }
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        private static class Native
        {
            private const string Dll = "vJoyInterface.dll";

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int GetVJDStatus(uint rID);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool AcquireVJD(uint rID);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void RelinquishVJD(uint rID);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool GetVJDAxisExist(uint rID, uint axis);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool GetVJDAxisMin(uint rID, uint axis, ref int min);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool SetAxis(int value, uint rID, uint axis);
        }
    }
}
