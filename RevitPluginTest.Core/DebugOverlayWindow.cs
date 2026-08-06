using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitPluginTest;
using Line = System.Windows.Shapes.Line;
using Ellipse = System.Windows.Shapes.Ellipse;
using Point = System.Windows.Point;

namespace RevitPluginTest.Core
{
    // Borderless, click-through, always-on-top window tracking the active
    // view's viewport rectangle, redrawn every Idling tick with whatever is
    // currently in DebugDraw, projected via ViewProjector. Hides itself when
    // there's no open document, the active view can't be projected, or Revit
    // isn't the foreground application (Topmost would otherwise float it
    // above other apps too).
    public sealed class DebugOverlayWindow : Window
    {
        private readonly Canvas _canvas = new();
        private string? _lastStatus;
        private static bool _dumpRequested;

        // Logs the raw projection inputs/outputs on the next tick - triggered
        // on demand (DumpOverlay()) rather than every tick, to avoid flooding
        // the log.
        public static void RequestDump() => _dumpRequested = true;

        public DebugOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            Content = _canvas;

            SourceInitialized += (sender, e) =>
            {
                Win32Interop.MakeClickThrough(new WindowInteropHelper(this).Handle);
            };

            // Forces the HWND to exist immediately, rather than lazily on
            // first Show(), so Update() can query DPI from the very first tick.
            new WindowInteropHelper(this).EnsureHandle();
        }

        public void Update(UIApplication uiApp)
        {
            DebugDraw.RemoveExpired(DateTime.Now);

            // Topmost means "above everything on the desktop," not just above
            // Revit - without this check it stays visible over other apps too.
            if (!Win32Interop.IsForegroundProcess())
            {
                Hide();
                return;
            }

            var uiDoc = uiApp.ActiveUIDocument;

            if (uiDoc == null)
            {
                OverlayState.IsViewSupported = false;
                SetStatus("Debug overlay hidden: no active document.");
                Hide();
                return;
            }

            var view = uiDoc.ActiveView;
            var uiView = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);

            if (uiView == null)
            {
                OverlayState.IsViewSupported = false;
                SetStatus("Debug overlay hidden: active view has no open UIView.");
                Hide();
                return;
            }

            if (!ViewProjector.IsSupported(view))
            {
                OverlayState.IsViewSupported = false;
                SetStatus("Debug overlay hidden: perspective 3D views can't be projected - switch to an orthographic view (plan/section/elevation, or a non-perspective 3D view).");
                Hide();
                return;
            }

            OverlayState.IsViewSupported = true;

            if (!OverlayState.UserWantsVisible)
            {
                SetStatus("Debug overlay hidden: turned off in the panel.");
                Hide();
                return;
            }

            SetStatus(null);

            var bounds = uiView.GetWindowRectangle();
            var dpiScale = Win32Interop.GetDpiScale(new WindowInteropHelper(this).Handle);

            if (_dumpRequested)
            {
                _dumpRequested = false;
                DumpProjectionState(view, uiView, bounds, dpiScale);
            }

            Left = bounds.Left / dpiScale;
            Top = bounds.Top / dpiScale;
            Width = Math.Max((bounds.Right - bounds.Left) / dpiScale, 0);
            Height = Math.Max((bounds.Bottom - bounds.Top) / dpiScale, 0);

            if (!IsVisible)
            {
                Show();
            }

            _canvas.Children.Clear();

            foreach (var line in DebugDraw.Lines)
            {
                if (ViewProjector.TryProject(view, uiView, line.Start, out var p1) &&
                    ViewProjector.TryProject(view, uiView, line.End, out var p2))
                {
                    _canvas.Children.Add(new Line
                    {
                        X1 = (p1.X - bounds.Left) / dpiScale,
                        Y1 = (p1.Y - bounds.Top) / dpiScale,
                        X2 = (p2.X - bounds.Left) / dpiScale,
                        Y2 = (p2.Y - bounds.Top) / dpiScale,
                        Stroke = new SolidColorBrush(line.Color),
                        StrokeThickness = line.Thickness
                    });
                }
            }

            foreach (var circle in DebugDraw.Circles)
            {
                var edgePoint = circle.Center + view.RightDirection.Multiply(circle.Radius);

                if (ViewProjector.TryProject(view, uiView, circle.Center, out var center) &&
                    ViewProjector.TryProject(view, uiView, edgePoint, out var edge))
                {
                    var pixelRadius = (edge - center).Length / dpiScale;

                    var ellipse = new Ellipse
                    {
                        Width = pixelRadius * 2,
                        Height = pixelRadius * 2,
                        Stroke = new SolidColorBrush(circle.Color),
                        StrokeThickness = circle.Thickness
                    };
                    Canvas.SetLeft(ellipse, (center.X - bounds.Left) / dpiScale - pixelRadius);
                    Canvas.SetTop(ellipse, (center.Y - bounds.Top) / dpiScale - pixelRadius);
                    _canvas.Children.Add(ellipse);
                }
            }

            foreach (var text in DebugDraw.Texts)
            {
                if (ViewProjector.TryProject(view, uiView, text.Position, out var p))
                {
                    var block = new TextBlock
                    {
                        Text = text.Text,
                        Foreground = new SolidColorBrush(text.Color),
                        FontSize = text.Thickness,
                        Background = Brushes.Black
                    };
                    Canvas.SetLeft(block, (p.X - bounds.Left) / dpiScale);
                    Canvas.SetTop(block, (p.Y - bounds.Top) / dpiScale);
                    _canvas.Children.Add(block);
                }
            }

            foreach (var arrow in DebugDraw.Arrows)
            {
                if (ViewProjector.TryProject(view, uiView, arrow.Start, out var p1) &&
                    ViewProjector.TryProject(view, uiView, arrow.End, out var p2))
                {
                    var start = new Point((p1.X - bounds.Left) / dpiScale, (p1.Y - bounds.Top) / dpiScale);
                    var end = new Point((p2.X - bounds.Left) / dpiScale, (p2.Y - bounds.Top) / dpiScale);
                    var brush = new SolidColorBrush(arrow.Color);

                    _canvas.Children.Add(new Line
                    {
                        X1 = start.X,
                        Y1 = start.Y,
                        X2 = end.X,
                        Y2 = end.Y,
                        Stroke = brush,
                        StrokeThickness = arrow.Thickness
                    });

                    AddArrowhead(_canvas, start, end, brush, arrow.Thickness);
                }
            }
        }

        // Draws a simple chevron ("V") arrowhead at tip, oriented along the
        // from->tip direction - computed in screen space (post-projection) so
        // it looks the same size regardless of zoom/distance.
        private static void AddArrowhead(Canvas canvas, Point from, Point tip, Brush stroke, double thickness)
        {
            var dx = tip.X - from.X;
            var dy = tip.Y - from.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);

            if (length < 1e-6)
            {
                return;
            }

            var backX = -dx / length;
            var backY = -dy / length;

            const double headLength = 12;
            const double headAngle = 0.45; // radians, ~26 degrees

            foreach (var angle in new[] { headAngle, -headAngle })
            {
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);
                var wingX = backX * cos - backY * sin;
                var wingY = backX * sin + backY * cos;

                canvas.Children.Add(new Line
                {
                    X1 = tip.X,
                    Y1 = tip.Y,
                    X2 = tip.X + wingX * headLength,
                    Y2 = tip.Y + wingY * headLength,
                    Stroke = stroke,
                    StrokeThickness = thickness
                });
            }
        }

        private static void DumpProjectionState(View view, UIView uiView, Autodesk.Revit.DB.Rectangle bounds, double dpiScale)
        {
            var corners = uiView.GetZoomCorners();

            Logger.Log($"[Dump] View '{view.Name}' ({view.GetType().Name}): Right={FormatXyz(view.RightDirection)} Up={FormatXyz(view.UpDirection)} ViewDir={FormatXyz(view.ViewDirection)}");

            if (view is View3D view3D)
            {
                var orientation = view3D.GetOrientation();
                Logger.Log($"[Dump] View3D.GetOrientation: Eye={FormatXyz(orientation.EyePosition)} Forward={FormatXyz(orientation.ForwardDirection)} Up={FormatXyz(orientation.UpDirection)}");
            }

            Logger.Log($"[Dump] ZoomCorners: BL={FormatXyz(corners[0])} TR={FormatXyz(corners[1])}");
            Logger.Log($"[Dump] WindowRect: ({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom}) dpiScale={dpiScale:0.00}");

            foreach (var circle in DebugDraw.Circles)
            {
                ViewProjector.TryProject(view, uiView, circle.Center, out var p);
                Logger.Log($"[Dump] Circle id={circle.Id} world={FormatXyz(circle.Center)} screen=({p.X:0.0}, {p.Y:0.0})");
            }

            foreach (var line in DebugDraw.Lines)
            {
                ViewProjector.TryProject(view, uiView, line.Start, out var p1);
                ViewProjector.TryProject(view, uiView, line.End, out var p2);
                Logger.Log($"[Dump] Line id={line.Id} start world={FormatXyz(line.Start)} screen=({p1.X:0.0}, {p1.Y:0.0}); end world={FormatXyz(line.End)} screen=({p2.X:0.0}, {p2.Y:0.0})");
            }
        }

        private static string FormatXyz(XYZ p) => $"({p.X:0.###}, {p.Y:0.###}, {p.Z:0.###})";

        private void SetStatus(string? status)
        {
            if (status == _lastStatus)
            {
                return;
            }

            _lastStatus = status;

            if (status != null)
            {
                Logger.Log(status);
            }
        }
    }
}
