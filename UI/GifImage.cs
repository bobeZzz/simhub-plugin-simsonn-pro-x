using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace SimsonnPedals.UI
{
    /// <summary>
    /// Plays an animated GIF embedded in this assembly. WPF's Image only shows the first frame, so frames are
    /// composited up front (GIF frames can be partial and have disposal rules) and swapped by an animation.
    /// </summary>
    public sealed class GifImage : Image
    {
        private ObjectAnimationUsingKeyFrames animation;

        public GifImage()
        {
            Stretch = Stretch.Uniform;
            Loaded += (s, e) => { if (animation != null) BeginAnimation(SourceProperty, animation); };
            Unloaded += (s, e) => BeginAnimation(SourceProperty, null);
        }

        /// <summary>Manifest resource name of the GIF, e.g. "SimsonnPedals.header.gif".</summary>
        public string ResourceName
        {
            get => null;
            set => Load(value);
        }

        private void Load(string resourceName)
        {
            try
            {
                using (var stream = typeof(GifImage).Assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return;
                    var frames = Compose(new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
                    if (frames.Count == 0) return;

                    Source = frames[0].Image;
                    if (frames.Count == 1) return;

                    animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                    var time = TimeSpan.Zero;
                    foreach (var frame in frames)
                    {
                        animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frame.Image, KeyTime.FromTimeSpan(time)));
                        time += frame.Delay;
                    }
                    animation.Duration = time;
                    animation.Freeze();
                    if (IsLoaded) BeginAnimation(SourceProperty, animation);
                }
            }
            catch (Exception ex)
            {
                // Decoration only: never let a bad image break the settings page.
                SimHub.Logging.Current.Error($"[SimsonnPedals] Could not load {resourceName}", ex);
            }
        }

        private static List<(BitmapSource Image, TimeSpan Delay)> Compose(GifBitmapDecoder decoder)
        {
            int width = Convert.ToInt32(decoder.Metadata?.GetQuery("/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth);
            int height = Convert.ToInt32(decoder.Metadata?.GetQuery("/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight);
            var full = new Rect(0, 0, width, height);
            var result = new List<(BitmapSource, TimeSpan)>();
            BitmapSource canvas = null;

            foreach (var frame in decoder.Frames)
            {
                var meta = frame.Metadata as BitmapMetadata;
                var rect = new Rect(Query(meta, "/imgdesc/Left"), Query(meta, "/imgdesc/Top"), frame.PixelWidth, frame.PixelHeight);
                int disposal = Query(meta, "/grctlext/Disposal");
                int delay = Query(meta, "/grctlext/Delay");

                var composed = Render(width, height, dc =>
                {
                    if (canvas != null) dc.DrawImage(canvas, full);
                    dc.DrawImage(frame, rect);
                });
                result.Add((composed, TimeSpan.FromMilliseconds(delay < 2 ? 100 : delay * 10)));

                // What the next frame is drawn on top of.
                switch (disposal)
                {
                    case 2: // restore to background: clear this frame's area
                        var previous = canvas;
                        canvas = previous == null ? null : Render(width, height, dc =>
                        {
                            dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(rect)));
                            dc.DrawImage(previous, full);
                            dc.Pop();
                        });
                        break;
                    case 3: // restore to previous: keep the canvas as it was before this frame
                        break;
                    default:
                        canvas = composed;
                        break;
                }
            }
            return result;
        }

        private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) draw(dc);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static int Query(BitmapMetadata meta, string query)
        {
            try
            {
                return meta?.GetQuery(query) is object value ? Convert.ToInt32(value) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
