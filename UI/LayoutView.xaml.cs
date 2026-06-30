// File: LayoutView.cs   (LayoutView.xaml.cs)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CoilOptimizer.UI
{
    public partial class LayoutView : UserControl
    {
        private LayoutVM Vm => DataContext as LayoutVM;

        private LayoutShape _hit, _drag;
        private Point _startScreen, _startInch;
        private double _origX, _origY;
        private const double DragThreshold = 4.0;
        // ---- handle dragging (pixel space) ----
        private HandleBand _hDrag;
        private Point _hStart;
        private double _hOrigX;
        private bool _hIsPattern;
        private List<LayoutShape> _dragShapes;
        public LayoutView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // Fixes "tiny until first resize": ViewportWidth/Height aren't valid
        // until the ScrollViewer has arranged its content, so push one update
        // after the initial layout pass.
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateViewport),
                                   DispatcherPriority.Loaded);
        }

        private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateViewport();

        private void UpdateViewport()
        {
            double w = Scroller.ViewportWidth > 0
                ? Scroller.ViewportWidth
                : Scroller.ActualWidth;

            // Use ActualHeight (stable) and let the VM reserve handles + scrollbar,
            // instead of ViewportHeight which fluctuates as the scrollbar appears.
            double h = Scroller.ActualHeight;

            if (Vm != null)
            {
                Vm.ScrollBarAllowance = SystemParameters.HorizontalScrollBarHeight;
                Vm.SetViewport(w, h);
            }
        }
        private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Vm == null) return;
            _hit = HitTestShape(e.GetPosition(Surface));
            _drag = null;
            _startScreen = e.GetPosition(this);
            _startInch = e.GetPosition(Surface);
            if (_hit != null && Vm.EditMode) { _origX = _hit.X; _origY = _hit.Y; }
            Surface.CaptureMouse();
        }

        private void Surface_MouseMove(object sender, MouseEventArgs e)
        {
            if (Vm == null || e.LeftButton != MouseButtonState.Pressed) return;
            if (_hit == null || !Vm.EditMode) return;

            var screen = e.GetPosition(this);
            if (_drag == null)
            {
                if (Math.Abs(screen.X - _startScreen.X) < DragThreshold &&
                    Math.Abs(screen.Y - _startScreen.Y) < DragThreshold)
                    return;
                _drag = _hit;
                _drag.ZIndex = 1000;      // bring to front
            }

            var p = e.GetPosition(Surface);
            _drag.X = _origX + (p.X - _startInch.X);
            _drag.Y = _origY + (p.Y - _startInch.Y);
        }

        private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (Vm == null) { Surface.ReleaseMouseCapture(); return; }
            Surface.ReleaseMouseCapture();

            if (_drag != null)
            {
                _drag.ZIndex = 0;         // send back
                double cx = _drag.X + _drag.Width / 2;
                double cy = _drag.Y + _drag.Height / 2;
                Vm.DropPart(_drag, cx, cy);
                _drag = null; _hit = null;
                return;
            }

            if (_hit != null) Vm.SelectPart(_hit);
            else
            {
                var p = e.GetPosition(Surface);
                Vm.SelectGapAt(p.X, p.Y);
            }
            _hit = null;
        }

        private LayoutShape HitTestShape(Point pt)
        {
            LayoutShape found = null;
            VisualTreeHelper.HitTest(Surface, null, r =>
            {
                var dep = r.VisualHit as DependencyObject;
                while (dep != null)
                {
                    if (dep is FrameworkElement fe && fe.Tag is LayoutShape s)
                    { found = s; return HitTestResultBehavior.Stop; }
                    dep = VisualTreeHelper.GetParent(dep);
                }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(pt));
            return found;
        }

        private void ColumnHandle_Down(object sender, MouseButtonEventArgs e) => BeginHandleDrag(sender, e, isPattern: false);

        private void PatternHandle_Down(object sender, MouseButtonEventArgs e) => BeginHandleDrag(sender, e, isPattern: true);

        private void BeginHandleDrag(object sender, MouseButtonEventArgs e, bool isPattern)
        {
            if (Vm == null || !Vm.EditMode) return;
            var canvas = (UIElement)sender;
            var band = HitTestHandle(e.GetPosition((IInputElement)canvas), canvas);
            if (band == null) return;

            _hDrag = band;
            _hIsPattern = isPattern;
            _hStart = e.GetPosition(this);
            _hOrigX = band.X;
            band.ZIndexBoost();                 // bring band visually forward (optional)
            canvas.CaptureMouse();

            _dragShapes = (isPattern
                ? Vm.ShapesForPattern(band.PatternId)
                : Vm.ShapesForColumn(band.Column)).ToList();
            foreach (var s in _dragShapes) s.OriginX = s.X;
            // wire move/up on the capturing canvas
            canvas.MouseMove += Handle_Move;
            canvas.MouseLeftButtonUp += Handle_Up;
        }

        private void Handle_Move(object sender, MouseEventArgs e)
        {
            if (_hDrag == null) return;
            var screen = e.GetPosition(this);

            double dxPx = screen.X - _hStart.X;
            _hDrag.X = _hOrigX + dxPx;                     // band (pixel space)

            // move the contents too (convert px delta -> inches)
            if (_dragShapes != null && Vm.ScaleX > 0)
            {
                Vm.NudgeShapes(_dragShapes, dxPx / Vm.ScaleX, z: 1000);
            }
        }

        private void Handle_Up(object sender, MouseButtonEventArgs e)
        {
            var canvas = (UIElement)sender;
            canvas.MouseMove -= Handle_Move;
            canvas.MouseLeftButtonUp -= Handle_Up;
            canvas.ReleaseMouseCapture();
            if (_hDrag == null || Vm == null) return;

            _hDrag.ZIndexReset();
            // center of the dragged band in pixels -> inches
            double centerPx = _hDrag.X + _hDrag.Width / 2;
            double centerIn = Vm.ScaleX > 0 ? centerPx / Vm.ScaleX : 0;

            if (_hIsPattern) Vm.MovePattern(_hDrag.PatternId, centerIn);
            else Vm.MoveColumn(_hDrag.Column, centerIn);

            _hDrag = null;
            _dragShapes = null; // Commit() rebuilds handles, snapping positions
        }

        private HandleBand HitTestHandle(Point pt, UIElement root)
        {
            HandleBand found = null;
            VisualTreeHelper.HitTest(root, null, r =>
            {
                var dep = r.VisualHit as DependencyObject;
                while (dep != null)
                {
                    if (dep is FrameworkElement fe && fe.Tag is HandleBand b)
                    { found = b; return HitTestResultBehavior.Stop; }
                    dep = VisualTreeHelper.GetParent(dep);
                }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(pt));
            return found;
        }
    }
}