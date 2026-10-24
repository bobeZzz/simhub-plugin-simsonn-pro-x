using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SimsonnPedals
{
    /// <summary>
    /// Thin vJoy feeder over vJoyInterface.dll. The DLL must match the installed vJoy driver, so the copy from
    /// the vJoy install folder is preloaded when present; SimHub's bundled copy is the fallback.
    /// Not thread-safe: only the input thread uses it.
    /// </summary>
    internal sealed class VJoyOutput : IDisposable
    {
        private const double RetryIntervalMs = 2000;

        private static bool libraryResolved;

        private readonly Dictionary<VJoyAxis, (int Min, int Max)> axisRanges = new Dictionary<VJoyAxis, (int, int)>();
        private readonly HashSet<VJoyAxis> writtenAxes = new HashSet<VJoyAxis>();
        private uint ownedId;
        private uint wantedId;
        private double nextAttemptMs;

        public string Status { get; private set; } = "Not started";
        public bool IsReady => ownedId != 0;

        /// <summary>Acquires vJoy device <paramref name="id"/>, retrying periodically until it succeeds.</summary>
        public void Ensure(uint id, double nowMs)
        {
            if (ownedId == id) return;
            if (wantedId != id)
            {
                Release();
                wantedId = id;
                nextAttemptMs = 0;
            }
            if (nowMs < nextAttemptMs) return;
            nextAttemptMs = nowMs + RetryIntervalMs;

            try
            {
                ResolveLibrary();
                if (!Native.vJoyEnabled())
                {
                    Status = "vJoy driver is not installed or not enabled";
                    return;
                }

                switch ((VjdStat)Native.GetVJDStatus(id))
                {
                    case VjdStat.Own:
                        break;
                    case VjdStat.Free:
                        if (!Native.AcquireVJD(id))
                        {
                            Status = $"Could not acquire vJoy device {id}";
                            return;
                        }
                        break;
                    case VjdStat.Busy:
                        Status = $"vJoy device {id} is in use by another program";
                        return;
                    case VjdStat.Missing:
                        Status = $"vJoy device {id} is not configured (run Configure vJoy)";
                        return;
                    default:
                        Status = $"vJoy device {id} is in an unknown state";
                        return;
                }

                // No ResetVJD here or on release: it may centre the axes, which for pedals means 50% throttle and brake.
                ownedId = id;
                axisRanges.Clear();
                writtenAxes.Clear();
                Status = $"vJoy device {id} acquired";
            }
            catch (DllNotFoundException)
            {
                Status = "vJoyInterface.dll not found; install vJoy";
            }
            catch (EntryPointNotFoundException)
            {
                Status = "vJoyInterface.dll is incompatible; reinstall vJoy";
            }
        }

        public void SetAxis(VJoyAxis axis, double value01)
        {
            if (ownedId == 0) return;

            if (!axisRanges.TryGetValue(axis, out var range))
            {
                if (!Native.GetVJDAxisExist(ownedId, (uint)axis))
                {
                    Status = $"vJoy device {ownedId} has no {axis} axis enabled";
                    range = (0, 0);
                }
                else
                {
                    int min = 0, max = 0x8000;
                    try { Native.GetVJDAxisMin(ownedId, (uint)axis, ref min); }
                    catch (EntryPointNotFoundException) { /* older vJoy builds: axes start at 0 */ }
                    Native.GetVJDAxisMax(ownedId, (uint)axis, ref max);
                    range = (min, max);
                }
                axisRanges[axis] = range;
            }
            if (range.Max <= range.Min) return;

            int value = range.Min + (int)Math.Round(PedalMath.Clamp01(value01) * (range.Max - range.Min));
            if (Native.SetAxis(value, ownedId, (uint)axis))
            {
                writtenAxes.Add(axis);
            }
            else
            {
                // Lost ownership (driver restart, another feeder): drop it and re-acquire on the next Ensure.
                Status = $"Lost vJoy device {ownedId}; retrying";
                ownedId = 0;
                nextAttemptMs = 0;
            }
        }

        /// <summary>Sets every axis this feeder has written back to 0 (pedal released).</summary>
        public void ZeroAll()
        {
            foreach (var axis in new List<VJoyAxis>(writtenAxes)) SetAxis(axis, 0);
        }

        /// <summary>Zeroes and forgets axes no longer in use, e.g. after a pedal is remapped or a preset switches axes.</summary>
        public void ZeroUnused(VJoyAxis inUse1, VJoyAxis inUse2)
        {
            List<VJoyAxis> stale = null;
            foreach (var axis in writtenAxes)
                if (axis != inUse1 && axis != inUse2) (stale ??= new List<VJoyAxis>()).Add(axis);
            if (stale == null) return;
            foreach (var axis in stale)
            {
                SetAxis(axis, 0);
                writtenAxes.Remove(axis);
            }
        }

        public void Release()
        {
            if (ownedId == 0) return;
            try
            {
                // vJoy keeps the last values after release, so leave the pedals at 0% rather than wherever they were.
                ZeroAll();
                Native.RelinquishVJD(ownedId);
            }
            catch (Exception)
            {
                // Releasing is best effort; the driver frees the device when the process exits anyway.
            }
            ownedId = 0;
            Status = "Released";
        }

        public void Dispose() => Release();

        private static readonly object libraryLock = new object();
        private static string libraryPath;

        /// <summary>Loads the vJoyInterface.dll matching the installed driver and returns its full path.</summary>
        internal static string ResolveLibrary()
        {
            lock (libraryLock)
            {
                if (libraryResolved) return libraryPath;
                libraryResolved = true;

                string arch = Environment.Is64BitProcess ? "x64" : "x86";
                foreach (var dir in VJoyInstallDirs())
                {
                    var path = Path.Combine(dir, arch, "vJoyInterface.dll");
                    if (File.Exists(path) && LoadLibrary(path) != IntPtr.Zero)
                    {
                        SimHub.Logging.Current.Info($"[SimsonnPedals] Using {path}");
                        return libraryPath = path;
                    }
                }
                SimHub.Logging.Current.Info("[SimsonnPedals] Using SimHub's bundled vJoyInterface.dll");
                return libraryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vJoyInterface.dll");
            }
        }

        private static IEnumerable<string> VJoyInstallDirs()
        {
            const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1";
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string location = null;
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = hklm.OpenSubKey(uninstallKey))
                        location = key?.GetValue("InstallLocation") as string;
                }
                catch (Exception)
                {
                    // Registry access is optional; fall through to the default path.
                }
                if (!string.IsNullOrEmpty(location)) yield return location;
            }
            yield return @"C:\Program Files\vJoy";
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        private enum VjdStat { Own = 0, Free = 1, Busy = 2, Missing = 3, Unknown = 4 }

        private static class Native
        {
            private const string Dll = "vJoyInterface.dll";

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool vJoyEnabled();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int GetVJDStatus(uint rID);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool AcquireVJD(uint rID);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void RelinquishVJD(uint rID);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool ResetVJD(uint rID);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool GetVJDAxisExist(uint rID, uint axis);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool GetVJDAxisMin(uint rID, uint axis, ref int min);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool GetVJDAxisMax(uint rID, uint axis, ref int max);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern bool SetAxis(int value, uint rID, uint axis);
        }
    }
}
