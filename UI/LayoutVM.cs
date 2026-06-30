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

    public class LanePart
    {
        public double Width;
        public double Length;
        public LanePart Clone() => new LanePart { Width = Width, Length = Length };
    }

    public class Col
    {
        public double Length;
        public List<LanePart> Lanes = new List<LanePart>();
        public bool LastIsManual;
        public int PatternId;                 // grouping for the pattern handle
        public double UsedWidth => Lanes.Sum(l => l.Width);

        /// <summary>Layout signature: same signature => same pattern.</summary>
        public string Signature() =>
            string.Join("|", Lanes.Select(l => $"{l.Width:0.####}x{l.Length:0.####}"));
    }

    public class HandleBand : ObservableObject
    {
        private double _x, _w;
        private int _z;
        public double X { get => _x; set => Set(ref _x, value); }   // px
        public double Width { get => _w; set => Set(ref _w, value); }
        public double Height => 10;                                 // px, fixed
        public int ZIndex { get => _z; set => Set(ref _z, value); } // NEW
        public string Label { get; set; }
        public int PatternId { get; set; }
        public Col Column { get; set; }
        public bool IsPattern { get; set; }

        public void ZIndexBoost() => ZIndex = 10000;  // bring to front while dragging
        public void ZIndexReset() => ZIndex = 1;
    }
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
        public bool IsManual { get; set; }    // pale-red status
        public LanePart Lane { get; set; }
        public Col Column { get; set; }
        public double OriginX { get; set; }
    }

    public class LayoutLine : ObservableObject
    {
        private double _x1, _y1, _x2, _y2;
        public double X1 { get => _x1; set => Set(ref _x1, value); }
        public double Y1 { get => _y1; set => Set(ref _y1, value); }
        public double X2 { get => _x2; set => Set(ref _x2, value); }
        public double Y2 { get => _y2; set => Set(ref _y2, value); }
        public bool IsPatternBoundary { get; set; }
    }

    public class LayoutChangedEventArgs : EventArgs
    {
        public double TotalLength { get; set; }
        public int ManualCutColumns { get; set; }
    }

    public class LayoutVM : ObservableObject
    {
        private const double Eps = 0.0005;

        public ObservableCollection<LayoutShape> Shapes { get; } =
            new ObservableCollection<LayoutShape>();
        public ObservableCollection<LayoutLine> ShearLines { get; } =
            new ObservableCollection<LayoutLine>();
        public ObservableCollection<LayoutLine> SlitLines { get; } =
            new ObservableCollection<LayoutLine>();
        public ObservableCollection<HandleBand> ColumnHandles { get; } =
    new ObservableCollection<HandleBand>();
        public ObservableCollection<HandleBand> PatternHandles { get; } =
            new ObservableCollection<HandleBand>();

        public Brush ColumnHandleBrush { get; set; } =
            new SolidColorBrush(Color.FromRgb(70, 130, 220));   // blue
        public Brush PatternHandleBrush { get; set; } =
            new SolidColorBrush(Color.FromRgb(40, 90, 170));    // darker blue

        // total height reserved by both handle bands (used to offset the surface)
        public double HandleBandHeight => 30; // 10 column + 10 pattern (px)
        public Brush FallOffBrush { get; set; } = new SolidColorBrush(Color.FromRgb(128, 128, 128));
        private readonly Brush _wheat = Freeze(Color.FromRgb(245, 222, 179));
        private readonly Brush _selDark = Freeze(Color.FromRgb(200, 170, 120));
        private readonly Brush _magenta = Freeze(Color.FromRgb(250, 200, 250));
        private readonly Brush _paleRed = Freeze(Color.FromRgb(255, 200, 200));
        public Brush ShearBrush { get; set; } = Brushes.Red;
        public Brush SlitBrush { get; set; } = Brushes.Black;
        public Brush GapBrush { get; set; } = new SolidColorBrush(Color.FromRgb(200, 255, 200));
        public ObservableCollection<LayoutLine> HandleBoundaryLines { get; } = new ObservableCollection<LayoutLine>();   // PIXEL space, spans both bands
        public Brush PatternBoundaryBrush { get; set; } = new SolidColorBrush(Color.FromRgb(255, 69, 0));  // OrangeRed

        public event EventHandler<LayoutChangedEventArgs> LayoutChanged;

        public ICommand ToggleScaleCommand { get; }
        public ICommand ToggleEditCommand { get; }

        public int MaxKnives { get; set; } = 8;   // => MaxKnives + 1 strips

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
        { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private double _baseW, _baseH;
        public double BaseWidth { get => _baseW; private set { if (Set(ref _baseW, value)) Raise(nameof(SurfacePixelWidth)); } }
        public double BaseHeight { get => _baseH; private set => Set(ref _baseH, value); }

        private double _scaleX = 1, _scaleY = 1;
        public double ScaleX { get => _scaleX; private set { if (Set(ref _scaleX, value)) { Raise(nameof(InvScaleX)); Raise(nameof(SurfacePixelWidth)); } } }
        public double ScaleY { get => _scaleY; private set { if (Set(ref _scaleY, value)) Raise(nameof(InvScaleY)); } }
        public double InvScaleX => _scaleX > 0 ? 1.0 / _scaleX : 1.0;
        public double InvScaleY => _scaleY > 0 ? 1.0 / _scaleY : 1.0;
        public double SurfacePixelWidth => BaseWidth * ScaleX;

        private double _shearTh = 1, _slitTh = 0.5, _hairTh = 1, _shearThP = 2;
        public double ShearThickness { get => _shearTh; private set => Set(ref _shearTh, value); }
        public double ShearPatternThickness { get => _shearThP; private set => Set(ref _shearThP, value); }
        public double SlitThickness { get => _slitTh; private set => Set(ref _slitTh, value); }
        public double HairlineThickness { get => _hairTh; private set => Set(ref _hairTh, value); }

        private bool _scaleEnabled = true, _editMode;
        // Toggle now just snaps the slider: ON => 1:1, OFF => fit.
        public bool ScaleEnabled
        {
            get => _scaleEnabled;
            set { if (Set(ref _scaleEnabled, value)) Zoom = value ? 1.0 : 0.0; }
        }
        public bool EditMode
        {
            get => _editMode;
            set { if (Set(ref _editMode, value) && !value) ClearSelection(); }
        }

        // 0 = fit whole length, 1 = true aspect (proportional to height)
        private double _zoom = 0.0;
        public double Zoom
        {
            get => _zoom;
            set { if (Set(ref _zoom, Math.Max(0, Math.Min(1, value)))) UpdateScale(); }
        }
        public double ZoomMin => 0.0;
        public double ZoomMax => 1.0;

        private double _gx, _gy, _gw, _gh;
        private Visibility _gapVis = Visibility.Collapsed;
        private string _gapLabel;
        public double GapX { get => _gx; private set => Set(ref _gx, value); }
        public double GapY { get => _gy; private set => Set(ref _gy, value); }
        public double GapW { get => _gw; private set => Set(ref _gw, value); }
        public double GapH { get => _gh; private set => Set(ref _gh, value); }
        public Visibility GapVisibility { get => _gapVis; private set => Set(ref _gapVis, value); }
        public string GapLabel { get => _gapLabel; private set => Set(ref _gapLabel, value); }
        public double ScrollBarAllowance { get; set; } = 17.0;

        // ---------------- load ----------------

        public void SetResult(CuttingResult result, decimal coilWidth)
        {
            _result = result;
            _coilWidth = (double)coilWidth;
            _selected = null;
            BuildColumns();
            RecomputePatterns();
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

        public void Rebuild()
        {
            Shapes.Clear(); ShearLines.Clear(); SlitLines.Clear();
            HideGap();
            if (_coilWidth <= 0 || _cols.Count == 0) { UpdateScale(); return; }

            UpdateColumnFlags();
            _totalLen = _cols.Sum(c => c.Length);
            BaseWidth = _totalLen;
            BaseHeight = _coilWidth;

            double x = 0;
            for (int k = 0; k < _cols.Count; k++)
            {
                var col = _cols[k];
                double y = 0;
                for (int i = 0; i < col.Lanes.Count; i++)
                {
                    var lane = col.Lanes[i];
                    bool manual = col.LastIsManual && i == col.Lanes.Count - 1;
                    Shapes.Add(new LayoutShape
                    {
                        X = x,
                        Y = y,
                        Width = lane.Length,
                        Height = lane.Width,
                        Lane = lane,
                        Column = col,
                        IsManual = manual,
                        Label = manual
                            ? $"{lane.Width:0.###} x {lane.Length:0.###}  (manual side cut)"
                            : $"{lane.Width:0.###} x {lane.Length:0.###}"
                    });
                    y += lane.Width;
                    if (i < col.Lanes.Count - 1)
                        SlitLines.Add(new LayoutLine
                        { X1 = x, Y1 = y, X2 = x + col.Length, Y2 = y });
                }

                bool patternEdge = k == 0 || _cols[k - 1].PatternId != col.PatternId;
                ShearLines.Add(new LayoutLine
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = _coilWidth,
                    IsPatternBoundary = patternEdge
                });
                x += col.Length;
            }

            // trailing edge is always a boundary (and a pattern boundary)
            ShearLines.Add(new LayoutLine
            {
                X1 = x,
                Y1 = 0,
                X2 = x,
                Y2 = _coilWidth,
                IsPatternBoundary = true
            });

            RefreshFills();
            UpdateScale();
        }
        private void UpdateColumnFlags()
        {
            foreach (var col in _cols)
            {
                bool hasFallOff = col.UsedWidth < _coilWidth - Eps;
                int knives = Math.Max(0, col.Lanes.Count - 1) + (hasFallOff ? 1 : 0);
                col.LastIsManual = col.Lanes.Count > 0 && knives > MaxKnives;
            }
        }

        private bool ColumnIsLocked(Col c)
        {
            bool hasFallOff = c.UsedWidth < _coilWidth - Eps;
            int knives = Math.Max(0, c.Lanes.Count - 1) + (hasFallOff ? 1 : 0);
            return knives > MaxKnives || c.Lanes.Count >= MaxKnives + 1;
        }

        public void UpdateScale()
        {
            if (_coilWidth <= 0) return;

            double reserved = HandleBandHeight + ScrollBarAllowance; // bands + h-scrollbar
            double usableH = Math.Max(1, _viewportH - reserved);
            ScaleY = usableH / _coilWidth;

            double fit = _totalLen > 0 ? _viewportW / _totalLen : ScaleY;
            double aspect = ScaleY;
            ScaleX = fit + (aspect - fit) * _zoom;

            ShearThickness = 1.5 / ScaleX;
            ShearPatternThickness = 3 / ScaleX;
            SlitThickness = 1.0 / ScaleY;
            HairlineThickness = 1.0 / ScaleY;

            RebuildHandles();
        }

        private void RebuildHandles()
        {
            ColumnHandles.Clear();
            PatternHandles.Clear();
            HandleBoundaryLines.Clear();
            if (_cols.Count == 0) return;

            // per-column blue handles (in pixels)
            double xIn = 0;
            foreach (var col in _cols)
            {
                double xPx = xIn * ScaleX;
                double wPx = col.Length * ScaleX;
                ColumnHandles.Add(new HandleBand
                {
                    X = xPx,
                    Width = wPx,
                    Column = col,
                    IsPattern = false,
                    Label = $"{col.Lanes.Count} strip(s)"
                });
                xIn += col.Length;
            }

            // pattern handles span contiguous columns sharing a PatternId
            xIn = 0;
            int i = 0;
            while (i < _cols.Count)
            {
                int pid = _cols[i].PatternId;
                double startIn = xIn;
                int count = 0;
                while (i < _cols.Count && _cols[i].PatternId == pid)
                {
                    xIn += _cols[i].Length;
                    count++; i++;
                }
                PatternHandles.Add(new HandleBand
                {
                    X = startIn * ScaleX,
                    Width = (xIn - startIn) * ScaleX,
                    PatternId = pid,
                    IsPattern = true,
                    Label = $"Pattern {pid} ×{count}"
                });
            }

            // pattern-boundary lines through the handle bands (pixels)
            xIn = 0;
            for (int k = 0; k < _cols.Count; k++)
            {
                bool edge = k == 0 || _cols[k - 1].PatternId != _cols[k].PatternId;
                if (edge)
                    HandleBoundaryLines.Add(new LayoutLine
                    {
                        X1 = xIn * ScaleX,
                        Y1 = 0,
                        X2 = xIn * ScaleX,
                        Y2 = HandleBandHeight,  // 20px
                        IsPatternBoundary = true
                    });
                xIn += _cols[k].Length;
            }
            // trailing
            HandleBoundaryLines.Add(new LayoutLine
            {
                X1 = xIn * ScaleX,
                Y1 = 0,
                X2 = xIn * ScaleX,
                Y2 = HandleBandHeight,
                IsPatternBoundary = true


            });
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
            _selected = null; HideGap(); RefreshFills();
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
                else if (s.IsManual)
                    s.Fill = _paleRed;
                else
                    s.Fill = _wheat;
            }
        }

        public void SelectGapAt(double cx, double cy)
        {
            _selected = null; RefreshFills();

            double x = 0; Col target = null;
            foreach (var col in _cols)
            {
                if (cx >= x && cx < x + col.Length) { target = col; break; }
                x += col.Length;
            }
            if (target == null) { HideGap(); return; }

            double y = 0;
            foreach (var lane in target.Lanes)
            {
                double laneEndX = x + lane.Length;
                if (cy >= y && cy < y + lane.Width)
                {
                    if (cx > laneEndX)
                        ShowGap(laneEndX, y, (x + target.Length) - laneEndX, lane.Width);
                    else
                        HideGap();
                    return;
                }
                y += lane.Width;
            }
            if (cy >= y && cy <= _coilWidth)
                ShowGap(x, y, target.Length, _coilWidth - y);
            else
                HideGap();
        }

        private void ShowGap(double gx, double gy, double gw, double gh)
        {
            if (gw <= Eps || gh <= Eps) { HideGap(); return; }
            GapX = gx; GapY = gy; GapW = gw; GapH = gh;
            GapLabel = $"{gh:0.###}\" x {gw:0.###}\"";
            GapVisibility = Visibility.Visible;
        }
        private void HideGap() => GapVisibility = Visibility.Collapsed;

        // ---------------- drop / edit ----------------

        public void DropPart(LayoutShape shape, double centerX, double centerY)
        {
            var lane = shape.Lane;
            var src = shape.Column;
            src.Lanes.Remove(lane);

            double x = 0; int ti = -1; double targetStart = 0;
            for (int i = 0; i < _cols.Count; i++)
            {
                if (centerX >= x && centerX < x + _cols[i].Length)
                { ti = i; targetStart = x; break; }
                x += _cols[i].Length;
            }

            if (ti < 0)
            {
                _cols.Add(new Col { Length = lane.Length, Lanes = { lane } });
                Cleanup(); Commit(); return;
            }

            var target = _cols[ti];
            double avail = _coilWidth - target.UsedWidth;
            bool fits = lane.Width <= avail + Eps
                        && target.Lanes.Count < MaxKnives + 1
                        && !ColumnIsLocked(target);

            if (fits)
            {
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
                double colCenter = targetStart + target.Length / 2;
                int insertAt = centerX < colCenter ? ti : ti + 1;
                _cols.Insert(insertAt, new Col { Length = lane.Length, Lanes = { lane } });
            }

            Cleanup(); Commit();
        }

        private void Cleanup() => _cols.RemoveAll(c => c.Lanes.Count == 0);

        private void Commit()
        {
            foreach (var c in _cols)
                if (c.Lanes.Count > 0) c.Length = c.Lanes.Max(l => l.Length);

            _selected = null;
            RecomputePatterns();   // <-- regroup before rebuild
            Rebuild();
            LayoutChanged?.Invoke(this, new LayoutChangedEventArgs
            {
                TotalLength = _totalLen,
                ManualCutColumns = _cols.Count(c => c.LastIsManual)
            });
        }

        /// <summary>
        /// Contiguous columns sharing an identical lane signature form one pattern.
        /// Re-run after any manual rearrange so handles auto-update.
        /// </summary>
        private void RecomputePatterns()
        {
            int pid = 0;
            string prevSig = null;
            foreach (var col in _cols)
            {
                string sig = col.Signature();
                if (prevSig == null || sig != prevSig) pid++;
                col.PatternId = pid;
                prevSig = sig;
            }
        }

        public void MoveColumn(Col col, double targetCenterXInches)
        {
            if (!_cols.Contains(col)) return;

            Col insertBefore = ResolveColumnDropTarget(col, targetCenterXInches);

            _cols.Remove(col);

            int idx = insertBefore == null ? _cols.Count : _cols.IndexOf(insertBefore);
            if (idx < 0) idx = _cols.Count;
            _cols.Insert(idx, col);

            Commit();
        }

        private Col ResolveColumnDropTarget(Col moving, double centerXInches)
        {
            double x = 0;
            Col firstOther = null;
            foreach (var c in _cols)
            {
                if (!ReferenceEquals(c, moving) && firstOther == null) firstOther = c;
                if (!ReferenceEquals(c, moving))
                {
                    double mid = x + c.Length / 2;
                    if (centerXInches < mid) return c;  // land before this column
                }
                x += c.Length; // advance using the visual (current) layout, incl. moving
            }
            return null; // past the end -> append
        }

        public void MovePattern(int patternId, double targetCenterXInches)
        {
            var block = _cols.Where(c => c.PatternId == patternId).ToList();
            if (block.Count == 0) return;

            // Decide insertion target in the ORIGINAL layout (visual coords),
            // skipping the block being moved. Returns the column the result
            // should sit *before* (a column reference, stable across removal).
            Col insertBefore = ResolvePatternDropTarget(patternId, targetCenterXInches);

            _cols.RemoveAll(c => c.PatternId == patternId);

            int idx = insertBefore == null ? _cols.Count : _cols.IndexOf(insertBefore);
            if (idx < 0) idx = _cols.Count;
            _cols.InsertRange(idx, block);

            Commit();
        }

        /// <summary>
        /// In the CURRENT (pre-removal) layout, find which pattern the drop center
        /// lands on (ignoring the moved pattern), then snap to the nearer side.
        /// Returns the column to insert before, or null to append at the end.
        /// </summary>
        private Col ResolvePatternDropTarget(int movingPid, double centerXInches)
        {
            // Build segments over the full current layout, but skip the moving block.
            double x = 0;
            int i = 0;
            var segs = new List<(int pid, double left, double right,
                                 Col firstCol, Col afterCol)>();

            while (i < _cols.Count)
            {
                int pid = _cols[i].PatternId;
                double left = x;
                int start = i;
                while (i < _cols.Count && _cols[i].PatternId == pid)
                {
                    x += _cols[i].Length;
                    i++;
                }
                Col afterCol = i < _cols.Count ? _cols[i] : null; // column following seg
                if (pid != movingPid)
                    segs.Add((pid, left, x, _cols[start], afterCol));
            }

            if (segs.Count == 0) return null; // nothing else to anchor to

            // Drop center before the first remaining segment.
            if (centerXInches < segs[0].left)
                return segs[0].firstCol;

            // Find the segment containing the center.
            foreach (var s in segs)
            {
                if (centerXInches >= s.left && centerXInches < s.right)
                {
                    double mid = (s.left + s.right) / 2;
                    // Left half -> before this segment; right half -> after it.
                    return centerXInches < mid ? s.firstCol : s.afterCol;
                }
            }

            // Past the last segment -> append.
            return null;
        }

        private static bool Eq(double a, double b) => Math.Abs(a - b) < Eps;

        public IEnumerable<LayoutShape> ShapesForColumn(Col col) => Shapes.Where(s => ReferenceEquals(s.Column, col));

        /// <summary>Shapes belonging to all columns of a pattern.</summary>
        public IEnumerable<LayoutShape> ShapesForPattern(int patternId)
            => Shapes.Where(s => s.Column != null && s.Column.PatternId == patternId);

        /// <summary>Apply a live inch-delta to a set of shapes and (optionally) raise them.</summary>
        public void NudgeShapes(IEnumerable<LayoutShape> shapes, double dxInches, int z)
        {
            foreach (var s in shapes)
            {
                s.X = s.OriginX + dxInches;   // see OriginX below
                s.ZIndex = z;
            }
        }
    }
}