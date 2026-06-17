// File: LayoutVM.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CoilOptimizer;

namespace CoilOptimizer.UI
{
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected bool Set<T>(ref T f, T v, [CallerMemberName] string n = null)
        {
            if (Equals(f, v)) return false;
            f = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            return true;
        }
        protected void Raise([CallerMemberName] string n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _e;
        public RelayCommand(Action e) { _e = e; }
        public event EventHandler CanExecuteChanged;
        public bool CanExecute(object p) => true;
        public void Execute(object p) => _e();
    }

    /// <summary>One physical strip in a column (inches). Unique instance per lane.</summary>
    public class LanePart
    {
        public double Width;
        public double Length;
        public LanePart Clone() => new LanePart { Width = Width, Length = Length };
    }

    /// <summary>A shear row = vertical band along the coil length (inches).</summary>
    public class Col
    {
        public double Length;                 // band length along coil
        public List<LanePart> Lanes = new List<LanePart>();
        public double UsedWidth => Lanes.Sum(l => l.Width);
    }

    /// <summary>A drawn part rectangle. Coordinates in INCHES.</summary>
    public class LayoutShape : ObservableObject
    {
        private double _x, _y, _w, _h;
        private int _z;
        private Brush _fill;
        public double X { get => _x; set => Set(ref _x, value); }
        public double Y { get => _y; set => Set(ref _y, value); }
        public double Width { get => _w; set => Set(ref _w, value); }
        public double Height { get => _h; set => Set(ref _h, value); }
        public int ZIndex { get => _z; set => Set(ref _z, value); }
        public Brush Fill { get => _fill; set => Set(ref _fill, value); }
        public string Label { get; set; }
        public LanePart Lane { get; set; }    // edit back-reference
        public Col Column { get; set; }
    }

    public class LayoutLine : ObservableObject
    {
        private double _x1, _y1, _x2, _y2;
        public double X1 { get => _x1; set => Set(ref _x1, value); }
        public double Y1 { get => _y1; set => Set(ref _y1, value); }
        public double X2 { get => _x2; set => Set(ref _x2, value); }
        public double Y2 { get => _y2; set => Set(ref _y2, value); }
    }

    public class LayoutChangedEventArgs : EventArgs
    {
        public double TotalLength { get; set; }
    }

    public class LayoutVM : ObservableObject
    {
        public ObservableCollection<LayoutShape> Shapes { get; } =
            new ObservableCollection<LayoutShape>();
        public ObservableCollection<LayoutLine> ShearLines { get; } =
            new ObservableCollection<LayoutLine>();
        public ObservableCollection<LayoutLine> SlitLines { get; } =
            new ObservableCollection<LayoutLine>();

        // brushes
        public Brush FallOffBrush { get; set; } = new SolidColorBrush(Color.FromRgb(128, 128, 128));
        private readonly Brush _wheat = Freeze(Color.FromRgb(245, 222, 179));
        private readonly Brush _selDark = Freeze(Color.FromRgb(200, 170, 120)); // darker wheat
        private readonly Brush _magenta = Freeze(Color.FromRgb(250, 200, 250)); // pale magenta
        public Brush ShearBrush { get; set; } = Brushes.Red;
        public Brush SlitBrush { get; set; } = Brushes.Black;
        public Brush GapBrush { get; set; } = new SolidColorBrush(Color.FromRgb(200, 255, 200)); // pale green

        public event EventHandler<LayoutChangedEventArgs> LayoutChanged;

        public ICommand ToggleScaleCommand { get; }
        public ICommand ToggleEditCommand { get; }

        private CuttingResult _result;
        private double _coilWidth, _totalLen = 1;
        private double _viewportW = 1, _viewportH = 1;
        private List<Col> _cols = new List<Col>();
        private LanePart _selected;

        public LayoutVM()
        {
            ToggleScaleCommand = new RelayCommand(() => ScaleEnabled = !ScaleEnabled);
            ToggleEditCommand = new RelayCommand(() => EditMode = !EditMode);
        }

        private static Brush Freeze(Color c)
        {
            var b = new SolidColorBrush(c); b.Freeze(); return b;
        }

        // base (inch) surface size
        private double _baseW, _baseH;
        public double BaseWidth { get => _baseW; private set => Set(ref _baseW, value); }
        public double BaseHeight { get => _baseH; private set => Set(ref _baseH, value); }

        // transform (independent X/Y)
        private double _scaleX = 1, _scaleY = 1;
        public double ScaleX { get => _scaleX; private set { if (Set(ref _scaleX, value)) Raise(nameof(InvScaleX)); } }
        public double ScaleY { get => _scaleY; private set { if (Set(ref _scaleY, value)) Raise(nameof(InvScaleY)); } }

        // inverse scale for constant-size text / strokes
        public double InvScaleX => _scaleX > 0 ? 1.0 / _scaleX : 1.0;
        public double InvScaleY => _scaleY > 0 ? 1.0 / _scaleY : 1.0;

        private double _shearTh = 2, _slitTh = 1, _hairTh = 1;
        public double ShearThickness { get => _shearTh; private set => Set(ref _shearTh, value); }
        public double SlitThickness { get => _slitTh; private set => Set(ref _slitTh, value); }
        public double HairlineThickness { get => _hairTh; private set => Set(ref _hairTh, value); }

        private bool _scaleEnabled = true, _editMode;
        public bool ScaleEnabled
        {
            get => _scaleEnabled;
            set { if (Set(ref _scaleEnabled, value)) { Raise(nameof(ZoomEnabled)); UpdateScale(); } }
        }
        public bool ZoomEnabled => _scaleEnabled;
        public bool EditMode
        {
            get => _editMode;
            set { if (Set(ref _editMode, value) && !value) { ClearSelection(); } }
        }

        // zoom slider: 0 = fit whole length, 1 = true aspect (proportional to height)
        private double _zoom = 0.0;
        public double Zoom
        {
            get => _zoom;
            set { if (Set(ref _zoom, Math.Max(0, Math.Min(1, value)))) UpdateScale(); }
        }
        public double ZoomMin => 0.0;
        public double ZoomMax => 1.0;

        // gap (offcut) highlight
        private double _gx, _gy, _gw, _gh;
        private Visibility _gapVis = Visibility.Collapsed;
        private string _gapLabel;
        public double GapX { get => _gx; private set => Set(ref _gx, value); }
        public double GapY { get => _gy; private set => Set(ref _gy, value); }
        public double GapW { get => _gw; private set => Set(ref _gw, value); }
        public double GapH { get => _gh; private set => Set(ref _gh, value); }
        public Visibility GapVisibility { get => _gapVis; private set => Set(ref _gapVis, value); }
        public string GapLabel { get => _gapLabel; private set => Set(ref _gapLabel, value); }

        // ---------------- load ----------------

        public void SetResult(CuttingResult result, decimal coilWidth)
        {
            _result = result;
            _coilWidth = (double)coilWidth;
            _selected = null;
            BuildColumns();
            Rebuild();
        }

        private void BuildColumns()
        {
            _cols = new List<Col>();
            if (_result == null) return;

            foreach (var p in _result.Patterns)
            {
                var template = p.Parts
                    .SelectMany(part => Enumerable.Repeat(part, part.Quantity))
                    .Select(part => new LanePart
                    { Width = (double)part.Width, Length = (double)part.Length })
                    .ToList();

                for (int r = 0; r < Math.Max(1, p.Repeat); r++)
                {
                    var col = new Col { Length = (double)p.Length };
                    col.Lanes.AddRange(template.Select(l => l.Clone()));
                    _cols.Add(col);
                }
            }
        }

        public void SetViewport(double w, double h)
        {
            _viewportW = w <= 0 ? 1 : w;
            _viewportH = h <= 0 ? 1 : h;
            UpdateScale();
        }

        /// <summary>Builds geometry from the column model. Called on load / drop only.</summary>
        public void Rebuild()
        {
            Shapes.Clear(); ShearLines.Clear(); SlitLines.Clear();
            HideGap();
            if (_coilWidth <= 0 || _cols.Count == 0) { UpdateScale(); return; }

            _totalLen = _cols.Sum(c => c.Length);
            BaseWidth = _totalLen;
            BaseHeight = _coilWidth;

            double x = 0;
            foreach (var col in _cols)
            {
                double y = 0;
                for (int i = 0; i < col.Lanes.Count; i++)
                {
                    var lane = col.Lanes[i];
                    Shapes.Add(new LayoutShape
                    {
                        X = x,
                        Y = y,
                        Width = lane.Length,
                        Height = lane.Width,
                        Fill = _wheat,
                        Lane = lane,
                        Column = col,
                        Label = $"{lane.Width:0.###} x {lane.Length:0.###}"
                    });
                    y += lane.Width;
                    if (i < col.Lanes.Count - 1)
                        SlitLines.Add(new LayoutLine
                        { X1 = x, Y1 = y, X2 = x + col.Length, Y2 = y });
                }
                // shear at the left edge of each column
                ShearLines.Add(new LayoutLine { X1 = x, Y1 = 0, X2 = x, Y2 = _coilWidth });
                x += col.Length;
            }
            // trailing shear
            ShearLines.Add(new LayoutLine { X1 = x, Y1 = 0, X2 = x, Y2 = _coilWidth });

            RefreshFills();
            UpdateScale();
        }

        /// <summary>Only updates transform scalars. No object churn.</summary>
        public void UpdateScale()
        {
            if (_coilWidth <= 0) return;

            ScaleY = _viewportH / _coilWidth;                 // ALWAYS fills height
            double fit = _totalLen > 0 ? _viewportW / _totalLen : ScaleY;
            double aspect = ScaleY;                            // true 1:1

            ScaleX = ScaleEnabled ? fit + (aspect - fit) * _zoom : fit;

            ShearThickness = 2.0 / ScaleX;  // vertical line width is along X
            SlitThickness = 1.0 / ScaleY;  // horizontal line height is along Y
            HairlineThickness = 1.0 / ScaleY;
        }

        // ---------------- selection ----------------

        public void SelectPart(LayoutShape shape)
        {
            HideGap();
            _selected = shape?.Lane;
            RefreshFills();
        }

        public void ClearSelection()
        {
            _selected = null;
            HideGap();
            RefreshFills();
        }

        private void RefreshFills()
        {
            foreach (var s in Shapes)
            {
                if (_selected != null && ReferenceEquals(s.Lane, _selected))
                    s.Fill = _selDark;
                else if (_selected != null &&
                         Eq(s.Lane.Width, _selected.Width) &&
                         Eq(s.Lane.Length, _selected.Length))
                    s.Fill = _magenta;
                else
                    s.Fill = _wheat;
            }
        }

        /// <summary>Click in gray space: find the offcut region and highlight it.</summary>
        public void SelectGapAt(double cx, double cy)
        {
            _selected = null;
            RefreshFills();

            double x = 0;
            Col target = null;
            foreach (var col in _cols)
            {
                if (cx >= x && cx < x + col.Length) { target = col; break; }
                x += col.Length;
            }

            if (target == null)
            {
                HideGap();
                return; // beyond layout (no trailing band modeled)
            }

            // vertical gap (unused width below the stacked lanes)
            double y = 0;
            foreach (var lane in target.Lanes)
            {
                double laneEndX = x + lane.Length;
                if (cy >= y && cy < y + lane.Width)
                {
                    if (cx > laneEndX) // gap to the right of a short (stacked) part
                    {
                        ShowGap(laneEndX, y, (x + target.Length) - laneEndX, lane.Width);
                        return;
                    }
                    HideGap(); // clicked on a part region (shouldn't normally happen)
                    return;
                }
                y += lane.Width;
            }

            // below all lanes => unused full-width offcut for this column
            if (cy >= y && cy <= _coilWidth)
                ShowGap(x, y, target.Length, _coilWidth - y);
            else
                HideGap();
        }

        private void ShowGap(double gx, double gy, double gw, double gh)
        {
            if (gw <= 0 || gh <= 0) { HideGap(); return; }
            GapX = gx; GapY = gy; GapW = gw; GapH = gh;
            GapLabel = $"{gh:0.###}\" x {gw:0.###}\"";
            GapVisibility = Visibility.Visible;
        }
        private void HideGap() => GapVisibility = Visibility.Collapsed;

        // ---------------- drop / edit ----------------

        /// <summary>
        /// Drop the dragged part. Uses its CENTER to pick the target column.
        /// If it doesn't fit, a new column is created left/right of center.
        /// </summary>
        public void DropPart(LayoutShape shape, double centerX, double centerY)
        {
            const double eps = 0.0001;
            var lane = shape.Lane;
            var src = shape.Column;

            src.Lanes.Remove(lane);

            // locate target column by center X (using current model, src lane removed)
            double x = 0; int ti = -1; double targetStart = 0;
            for (int i = 0; i < _cols.Count; i++)
            {
                if (centerX >= x && centerX < x + _cols[i].Length)
                { ti = i; targetStart = x; break; }
                x += _cols[i].Length;
            }
            if (ti < 0) // dropped past the end -> append new column
            {
                _cols.Add(new Col { Length = lane.Length, Lanes = { lane } });
                Cleanup(); Commit(); return;
            }

            var target = _cols[ti];
            double avail = _coilWidth - target.UsedWidth;

            if (lane.Width <= avail + eps)
            {
                // insert at lane index based on center Y
                int idx = target.Lanes.Count; double y = 0;
                for (int k = 0; k < target.Lanes.Count; k++)
                {
                    if (centerY < y + target.Lanes[k].Width / 2) { idx = k; break; }
                    y += target.Lanes[k].Width;
                }
                target.Lanes.Insert(idx, lane);
            }
            else
            {
                // no room -> new column to the side the center favors
                double colCenter = targetStart + target.Length / 2;
                int insertAt = centerX < colCenter ? ti : ti + 1;
                _cols.Insert(insertAt, new Col { Length = lane.Length, Lanes = { lane } });
            }

            Cleanup(); Commit();
        }

        private void Cleanup() => _cols.RemoveAll(c => c.Lanes.Count == 0);

        private void Commit()
        {
            _selected = null;
            Rebuild();
            LayoutChanged?.Invoke(this,
                new LayoutChangedEventArgs { TotalLength = _totalLen });
        }

        private static bool Eq(double a, double b) => Math.Abs(a - b) < 0.0005;
    }
}