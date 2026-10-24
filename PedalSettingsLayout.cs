using System;
using System.Linq;

namespace SimsonnPedals
{
    /// <summary>
    /// Fields of the pedals' settings block that the plugin understands, as mapped from Simsonn Manager
    /// one-change tests (research/pedal-settings-layout.md). Everything else is passed through untouched.
    /// </summary>
    public static class PedalSettingsLayout
    {
        public const int ThrottleAxis = 0;
        public const int BrakeAxis = 1;

        /// <summary>Raw load-cell units per kg of max force on a calibrated brake.</summary>
        public const double UnitsPerKg = 300;

        private const int AxisBase = 0x40;
        private const int AxisSize = 52;
        private const int OutLow = 0, OutCentre = 2, OutHigh = 4, RawMin = 6, RawMax = 8;
        private const int Flags = 10, Curve = 11, CurvePoints = 11, DeadzoneStart = 34, DeadzoneEndGap = 35;
        private const int FilterMask = 0xE0, FilterShift = 5;

        public sealed class AxisSettings
        {
            public int RawMin { get; set; }
            public int RawMax { get; set; }
            public int DeadzoneStart { get; set; }
            public int DeadzoneEnd { get; set; }
            public int Filter { get; set; }
            public sbyte[] Curve { get; set; }

            public bool IsLinear => Curve.Select((p, i) => p == -100 + 20 * i).All(x => x);
        }

        public static AxisSettings ReadAxis(byte[] block, int axis)
        {
            int o = AxisBase + AxisSize * axis;
            return new AxisSettings
            {
                RawMin = BitConverter.ToInt16(block, o + RawMin),
                RawMax = BitConverter.ToInt16(block, o + RawMax),
                DeadzoneStart = block[o + DeadzoneStart],
                DeadzoneEnd = 100 - block[o + DeadzoneEndGap],
                Filter = (block[o + Flags] & FilterMask) >> FilterShift,
                Curve = Enumerable.Range(0, CurvePoints).Select(i => unchecked((sbyte)block[o + Curve + i])).ToArray(),
            };
        }

        /// <summary>Max force the brake is set to, in kg (span between rest and full press / 300).</summary>
        public static double BrakeMaxForceKg(byte[] block)
        {
            var brake = ReadAxis(block, BrakeAxis);
            return Math.Round((brake.RawMax - brake.RawMin) / UnitsPerKg, 1);
        }

        /// <summary>One line per pedal, in the terms Simsonn Manager uses.</summary>
        public static string Describe(byte[] block)
        {
            string Axis(string name, int axis, string extra)
            {
                var a = ReadAxis(block, axis);
                string curve = a.IsLinear ? "linear" : "custom curve";
                string filter = a.Filter == 0 ? "filter off" : $"filter {a.Filter}";
                return $"{name}: dead zone {a.DeadzoneStart}–{a.DeadzoneEnd}, {curve}, {filter}{extra}";
            }
            return Axis("Throttle", ThrottleAxis, "") + Environment.NewLine +
                   Axis("Brake", BrakeAxis, $", max force {BrakeMaxForceKg(block):0.#} kg");
        }

        /// <summary>True if the block already matches what <see cref="BaseSetup"/> would produce.</summary>
        public static bool IsBaseSetup(byte[] block, double brakeMaxForceKg) =>
            BaseSetup(block, brakeMaxForceKg).SequenceEqual(block);

        /// <summary>
        /// Copy of <paramref name="block"/> with throttle and brake set up for shaping in the plugin: linear curve,
        /// no dead zone, filter off, and the brake's max force set to <paramref name="brakeMaxForceKg"/>.
        /// Calibration (each pedal's rest value) is kept.
        /// </summary>
        public static byte[] BaseSetup(byte[] block, double brakeMaxForceKg)
        {
            var result = (byte[])block.Clone();
            foreach (int axis in new[] { ThrottleAxis, BrakeAxis })
            {
                int o = AxisBase + AxisSize * axis;
                int rawMin = BitConverter.ToInt16(block, o + RawMin);
                int rawMax = BitConverter.ToInt16(block, o + RawMax);
                if (axis == BrakeAxis)
                {
                    long target = rawMin + (long)Math.Round(brakeMaxForceKg * UnitsPerKg);
                    if (brakeMaxForceKg < 5 || target > short.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(brakeMaxForceKg),
                            $"Max force must be between 5 and {MaxBrakeForceKg(block):0} kg for this calibration.");
                    rawMax = (int)target;
                    WriteInt16(result, o + RawMax, rawMax);
                }

                // No dead zone: the output range is the whole calibrated range.
                WriteInt16(result, o + OutLow, rawMin);
                WriteInt16(result, o + OutHigh, rawMax);
                WriteInt16(result, o + OutCentre, (rawMin + rawMax) / 2);
                result[o + DeadzoneStart] = 0;
                result[o + DeadzoneEndGap] = 0;
                result[o + Flags] = (byte)(block[o + Flags] & ~FilterMask);
                for (int i = 0; i < CurvePoints; i++) result[o + Curve + i] = unchecked((byte)(sbyte)(-100 + 20 * i));
            }
            return result;
        }

        /// <summary>Highest max force the brake's calibration leaves room for.</summary>
        public static double MaxBrakeForceKg(byte[] block) =>
            Math.Floor((short.MaxValue - ReadAxis(block, BrakeAxis).RawMin) / UnitsPerKg);

        private static void WriteInt16(byte[] block, int offset, int value)
        {
            var bytes = BitConverter.GetBytes((short)value);
            block[offset] = bytes[0];
            block[offset + 1] = bytes[1];
        }
    }
}
