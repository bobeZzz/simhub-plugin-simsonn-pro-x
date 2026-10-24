using System;
using System.Windows;
using System.Windows.Media;

namespace SimsonnPedals.UI
{
    /// <summary>Plain horizontal level bar. Unlike a themed ProgressBar it never animates, so it tracks the pedal 1:1.</summary>
    public sealed class LevelBar : FrameworkElement
    {
        private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF)));

        private double value;
        private double drawnValue = -1;

        /// <summary>Fill level, 0..1.</summary>
        public double Value
        {
            get => value;
            set
            {
                this.value = PedalMath.Clamp01(double.IsNaN(value) ? 0 : value);
                // Skip redraws smaller than half a pixel, measured against what is on screen.
                if (Math.Abs(this.value - drawnValue) * ActualWidth >= 0.5 || (this.value == 0) != (drawnValue == 0))
                    InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            drawnValue = value;
            double radius = ActualHeight / 2;
            dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
            if (value > 0)
                dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(ActualHeight, ActualWidth * value), ActualHeight), radius, radius);
        }

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
