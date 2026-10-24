using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimsonnPedals.UI
{
    /// <summary>11-point response curve with draggable points and a live marker, styled after Simsonn Manager.</summary>
    public sealed class CurveEditor : FrameworkElement
    {
        private const double MarginLeft = 30, MarginBottom = 20, MarginTop = 8, MarginRight = 8;
        private const double PointSize = 9, HitRadius = 12;

        private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)));
        private static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1));
        private static readonly Pen LinearPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });
        private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF)));
        private static readonly Pen CurvePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0xB3, 0xFF)), 2));
        private static readonly Pen PointPen = Frozen(new Pen(Brushes.White, 1));
        private static readonly Brush LiveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x1C)));
        private static readonly Pen LivePen = Frozen(new Pen(LiveBrush, 1) { DashStyle = DashStyles.Dot });
        private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
        private static readonly Typeface LabelFace = new Typeface("Segoe UI");

        private PedalConfig config;
        private int dragIndex = -1;
        private double liveInput = double.NaN, liveOutput = double.NaN;

        // Grid, labels and curve only change on edits or resizes; the live marker moves every frame.
        private DrawingGroup staticLayer;

        public CurveEditor()
        {
            MinHeight = 220;
            Cursor = Cursors.Arrow;
        }

        public PedalConfig Config
        {
            get => config;
            set
            {
                if (config != null) config.PropertyChanged -= OnConfigChanged;
                config = value;
                if (config != null) config.PropertyChanged += OnConfigChanged;
                InvalidateStatic();
            }
        }

        /// <summary>Updates the live marker (both 0..1). Pass NaN to hide it.</summary>
        public void SetLive(double input, double output)
        {
            if (Math.Abs(input - liveInput) < 0.002 && Math.Abs(output - liveOutput) < 0.002) return;
            liveInput = input;
            liveOutput = output;
            InvalidateVisual();
        }

        private void OnConfigChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PedalConfig.CurvePoints)) InvalidateStatic();
        }

        private void InvalidateStatic()
        {
            staticLayer = null;
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            staticLayer = null;
        }

        private Rect Plot => new Rect(MarginLeft, MarginTop,
            Math.Max(1, ActualWidth - MarginLeft - MarginRight),
            Math.Max(1, ActualHeight - MarginTop - MarginBottom));

        private Point ToScreen(double x01, double y01)
        {
            var p = Plot;
            return new Point(p.Left + x01 * p.Width, p.Bottom - y01 * p.Height);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (staticLayer == null)
            {
                staticLayer = new DrawingGroup();
                using (var sdc = staticLayer.Open()) RenderStatic(sdc);
                staticLayer.Freeze();
            }
            dc.DrawDrawing(staticLayer);

            var plot = Plot;
            if (config != null && dragIndex >= 0)
            {
                var points = config.CurvePoints;
                var c = ToScreen(dragIndex / 10.0, points[dragIndex] / 100);
                var text = Label($"{dragIndex * 10}% → {points[dragIndex]:0}%", VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(Math.Min(c.X + 8, plot.Right - text.Width), Math.Max(c.Y - 20, plot.Top)));
            }

            if (!double.IsNaN(liveInput))
            {
                var live = ToScreen(liveInput, liveOutput);
                dc.DrawLine(LivePen, new Point(live.X, plot.Bottom), live);
                dc.DrawEllipse(LiveBrush, null, live, 5, 5);
            }
        }

        private void RenderStatic(DrawingContext dc)
        {
            var plot = Plot;
            dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            for (int i = 0; i <= 10; i++)
            {
                double x = plot.Left + plot.Width * i / 10;
                double y = plot.Bottom - plot.Height * i / 10;
                dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                if (i % 2 == 0)
                {
                    var label = (i * 10).ToString(CultureInfo.InvariantCulture);
                    var yText = Label(label, dpi);
                    dc.DrawText(yText, new Point(plot.Left - yText.Width - 4, y - yText.Height / 2));
                    var xText = Label(label, dpi);
                    dc.DrawText(xText, new Point(x - xText.Width / 2, plot.Bottom + 3));
                }
            }
            dc.DrawLine(LinearPen, plot.BottomLeft, plot.TopRight);

            if (config == null) return;
            var points = config.CurvePoints;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(ToScreen(0, points[0] / 100), false, false);
                for (int i = 1; i < points.Length; i++)
                    ctx.LineTo(ToScreen(i / 10.0, points[i] / 100), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, CurvePen, geometry);

            for (int i = 0; i < points.Length; i++)
            {
                var c = ToScreen(i / 10.0, points[i] / 100);
                dc.DrawRoundedRectangle(Accent, PointPen, new Rect(c.X - PointSize / 2, c.Y - PointSize / 2, PointSize, PointSize), 2, 2);
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (config == null) return;
            var pos = e.GetPosition(this);
            var points = config.CurvePoints;
            double best = HitRadius;
            dragIndex = -1;
            for (int i = 0; i < points.Length; i++)
            {
                var c = ToScreen(i / 10.0, points[i] / 100);
                double d = (c - pos).Length;
                if (d <= best)
                {
                    best = d;
                    dragIndex = i;
                }
            }
            if (dragIndex < 0) return;
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragIndex < 0)
            {
                Cursor = NearPoint(e.GetPosition(this)) ? Cursors.SizeNS : Cursors.Arrow;
                return;
            }
            var plot = Plot;
            double y01 = (plot.Bottom - e.GetPosition(this).Y) / plot.Height;
            config.SetCurvePoint(dragIndex, Math.Round(y01 * 100));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (dragIndex < 0) return;
            dragIndex = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            dragIndex = -1;
            InvalidateVisual();
        }

        private bool NearPoint(Point pos)
        {
            if (config == null) return false;
            var points = config.CurvePoints;
            for (int i = 0; i < points.Length; i++)
                if ((ToScreen(i / 10.0, points[i] / 100) - pos).Length <= HitRadius) return true;
            return false;
        }

        private FormattedText Label(string text, double dpi) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10, LabelBrush, dpi);

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
