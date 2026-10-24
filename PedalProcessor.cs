using System;

namespace SimsonnPedals
{
    /// <summary>Pure pedal math, kept free of SimHub/DirectInput types so it can be tested on its own.</summary>
    public static class PedalMath
    {
        /// <summary>Maps a raw axis value into 0..1 using the calibrated range.</summary>
        public static double Normalize(int raw, int calibrationMin, int calibrationMax, bool invert)
        {
            int lo = Math.Min(calibrationMin, calibrationMax);
            int hi = Math.Max(calibrationMin, calibrationMax);
            double x = hi > lo ? (raw - lo) / (double)(hi - lo) : 0;
            x = Clamp01(x);
            return invert ? 1 - x : x;
        }

        /// <summary>
        /// Rescales a load-cell position (0..1 of the firmware max force) so that <paramref name="maxForceKg"/> maps to 1.
        /// A target above the firmware value can't be reached, so it is treated as the firmware value.
        /// </summary>
        public static double ApplyMaxForce(double x, double firmwareMaxForceKg, double maxForceKg)
        {
            if (firmwareMaxForceKg <= 0 || maxForceKg <= 0) return x;
            return Clamp01(x * firmwareMaxForceKg / Math.Min(maxForceKg, firmwareMaxForceKg));
        }

        /// <summary>Rescales 0..1 so that everything below <paramref name="lowPercent"/> is 0 and above <paramref name="highPercent"/> is 1.</summary>
        public static double ApplyDeadzone(double x, double lowPercent, double highPercent)
        {
            double lo = lowPercent / 100.0;
            double hi = highPercent / 100.0;
            if (hi <= lo) return x >= hi ? 1 : 0;
            return Clamp01((x - lo) / (hi - lo));
        }

        /// <summary>Piecewise-linear lookup through evenly spaced points (output percent at input 0%, 10%, ... 100%).</summary>
        public static double EvaluateCurve(double[] points, double x)
        {
            x = Clamp01(x);
            int segments = points.Length - 1;
            double pos = x * segments;
            int i = Math.Min((int)pos, segments - 1);
            double t = pos - i;
            return Clamp01((points[i] + (points[i + 1] - points[i]) * t) / 100.0);
        }

        public static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
    }

    /// <summary>Per-pedal runtime state: calibration capture and the latest values for the UI.</summary>
    public sealed class PedalProcessor
    {
        private volatile bool calibrating;

        public int Raw { get; private set; }

        /// <summary>Calibrated position, 0..1, before dead zone and curve.</summary>
        public double Position { get; private set; }

        /// <summary>Dead-zoned value fed into the curve, 0..1.</summary>
        public double CurveInput { get; private set; }

        /// <summary>Final value sent to vJoy, 0..1.</summary>
        public double Output { get; private set; }

        public bool IsCalibrating => calibrating;
        public int CalibrationSeenMin { get; private set; }
        public int CalibrationSeenMax { get; private set; }

        public void StartCalibration()
        {
            CalibrationSeenMin = Raw;
            CalibrationSeenMax = Raw;
            calibrating = true;
        }

        /// <summary>Stops capture and writes the observed range into <paramref name="config"/> if the pedal actually moved.</summary>
        public bool FinishCalibration(PedalConfig config)
        {
            calibrating = false;
            if (CalibrationSeenMax - CalibrationSeenMin < 1000) return false;
            config.CalibrationMin = CalibrationSeenMin;
            config.CalibrationMax = CalibrationSeenMax;
            return true;
        }

        public void CancelCalibration() => calibrating = false;

        /// <summary>
        /// Direct mode: the pedals shape the axis themselves, so their output is the final value. The position before
        /// shaping isn't known here, so it shows the output too, and the curve gets no live marker.
        /// </summary>
        public double ShowDirect(int raw)
        {
            Raw = raw;
            Output = Position = PedalMath.Clamp01(raw / (double)PedalConfig.RawMax);
            CurveInput = double.NaN;
            return Output;
        }

        public double Process(PedalConfig config, int raw)
        {
            Raw = raw;
            if (calibrating)
            {
                if (raw < CalibrationSeenMin) CalibrationSeenMin = raw;
                if (raw > CalibrationSeenMax) CalibrationSeenMax = raw;
            }

            double x = PedalMath.Normalize(raw, config.CalibrationMin, config.CalibrationMax, config.Invert);
            Position = x;
            x = PedalMath.ApplyMaxForce(x, config.FirmwareMaxForceKg, config.MaxForceKg);

            CurveInput = PedalMath.ApplyDeadzone(x, config.DeadzoneLow, config.DeadzoneHigh);
            Output = PedalMath.EvaluateCurve(config.CurvePoints, CurveInput);
            return Output;
        }
    }
}
