using System;
using System.Linq;
using System.Threading;
using HidSharp;

namespace SimsonnPedals
{
    /// <summary>
    /// Streams the pedals' raw sensor values from FreeJoy's params report (ID 2) on the settings interface, as Simsonn
    /// Manager's calibration dialog does (research/manager-protocol.md, confirmed on the rig with 1.0.6). Asking for
    /// params is read-only: the only thing this class sends is the params request [2].
    /// </summary>
    public sealed class PedalRawMonitor : IDisposable
    {
        public const int Channels = 3; // throttle, brake, clutch

        private const byte ReportIdParams = 2;
        private const int RequestEveryMs = 1000;

        private readonly HidStream stream;
        private readonly byte[] request;
        private readonly Thread thread;
        private readonly object sync = new object();
        private readonly int[] latest = new int[Channels];
        private readonly int[] min = new int[Channels];
        private readonly int[] max = new int[Channels];
        private volatile bool running = true;

        /// <summary>Packets with raw values received since <see cref="ResetRange"/>.</summary>
        public int Samples { get; private set; }

        /// <summary>Set when the stream failed; the monitor has stopped.</summary>
        public string Error { get; private set; }

        private PedalRawMonitor(HidDevice device, HidStream stream)
        {
            this.stream = stream;
            stream.ReadTimeout = 100;
            request = new byte[device.GetMaxOutputReportLength()];
            request[0] = ReportIdParams;
            ResetRange();
            thread = new Thread(Run) { IsBackground = true, Name = "SimsonnPedals raw monitor" };
            thread.Start();
        }

        /// <summary>Opens the pedals' settings interface and starts streaming; null (with <paramref name="error"/>) if it can't.</summary>
        public static PedalRawMonitor Open(int vendorId, int productId, out string error)
        {
            var log = new System.Text.StringBuilder();
            var device = PedalConfigReader.FindInterfaces(DeviceList.Local.GetHidDevices(vendorId, productId), ReportIdParams, log).FirstOrDefault();
            if (device == null)
            {
                error = "the pedals' settings interface wasn't found.";
                return null;
            }
            if (!device.TryOpen(out HidStream stream))
            {
                error = "couldn't open the pedals' settings interface (in use by another program?).";
                return null;
            }
            error = null;
            return new PedalRawMonitor(device, stream);
        }

        /// <summary>Current raw value of each channel.</summary>
        public int[] Latest { get { lock (sync) return (int[])latest.Clone(); } }

        /// <summary>Lowest raw value of each channel since <see cref="ResetRange"/>.</summary>
        public int[] Min { get { lock (sync) return (int[])min.Clone(); } }

        /// <summary>Highest raw value of each channel since <see cref="ResetRange"/>.</summary>
        public int[] Max { get { lock (sync) return (int[])max.Clone(); } }

        public void ResetRange()
        {
            lock (sync)
            {
                for (int i = 0; i < Channels; i++)
                {
                    min[i] = int.MaxValue;
                    max[i] = int.MinValue;
                }
                Samples = 0;
            }
        }

        private void Run()
        {
            var reply = new byte[stream.Device.GetMaxInputReportLength()];
            var nextRequest = DateTime.UtcNow;
            try
            {
                while (running)
                {
                    if (DateTime.UtcNow >= nextRequest)
                    {
                        stream.Write(request);
                        nextRequest = DateTime.UtcNow.AddMilliseconds(RequestEveryMs);
                    }

                    int count;
                    try
                    {
                        count = stream.Read(reply);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    // Packet 0 of the params struct: raw channel i at byte 6 + 2i. Packet 1 is junk.
                    if (count < 6 + 2 * Channels || reply[0] != ReportIdParams || reply[1] != 0) continue;
                    lock (sync)
                    {
                        for (int i = 0; i < Channels; i++)
                        {
                            int raw = BitConverter.ToInt16(reply, 6 + 2 * i);
                            latest[i] = raw;
                            if (raw < min[i]) min[i] = raw;
                            if (raw > max[i]) max[i] = raw;
                        }
                        Samples++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (running) Error = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        public void Dispose()
        {
            running = false;
            thread.Join(500);
            stream.Dispose();
        }
    }
}
