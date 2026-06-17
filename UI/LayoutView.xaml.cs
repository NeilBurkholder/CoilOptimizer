// File: LayoutView.cs   (LayoutView.xaml.cs)
using System;
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
            double w = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
            double h = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
            Vm?.SetViewport(w, h);
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
    }
}