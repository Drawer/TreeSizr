using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TreeSizr.Scanning;

namespace TreeSizr.Controls;

/// <summary>A rectangle drawn on the treemap. <see cref="IsFiles"/> marks the block for the loose files of <see cref="Node"/>.</summary>
public sealed record TreemapHit(DirNode Node, bool IsFiles, Rect Bounds, int Depth);

/// <summary>
/// Squarified treemap of a directory, nested a few levels deep.
/// Each top-level subdirectory gets its own hue; deeper levels are lighter shades of it.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(DirNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(SizeMetric), typeof(TreemapControl),
        new FrameworkPropertyMetadata(SizeMetric.OnDisk, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int MaxDepth = 3;
    private const double LabelHeight = 16;
    private const double MinBlock = 3;

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x4E, 0x79, 0xA7), Color.FromRgb(0xF2, 0x8E, 0x2B), Color.FromRgb(0x59, 0xA1, 0x4F),
        Color.FromRgb(0xE1, 0x57, 0x59), Color.FromRgb(0x76, 0xB7, 0xB2), Color.FromRgb(0xED, 0xC9, 0x48),
        Color.FromRgb(0xB0, 0x7A, 0xA1), Color.FromRgb(0xFF, 0x9D, 0xA7), Color.FromRgb(0x9C, 0x75, 0x5F),
        Color.FromRgb(0x86, 0xBC, 0xB6),
    ];

    private static readonly Color FilesColor = Color.FromRgb(0xA0, 0xA0, 0xA0);
    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));
    private static readonly Pen BorderPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x20, 0x20, 0x20)), 1));
    private static readonly Pen HoverPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Typeface LabelFont = new("Segoe UI");

    private readonly Dictionary<(int, int), Brush> _brushCache = [];
    private readonly List<TreemapHit> _hits = [];
    private TreemapHit? _hover;

    public TreemapControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    public DirNode? Root
    {
        get => (DirNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    /// <summary>Block areas follow the file size or the space on disk.</summary>
    public SizeMetric Metric
    {
        get => (SizeMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    /// <summary>Raised when the user clicks a subdirectory; the argument is the direct child of <see cref="Root"/> that contains it.</summary>
    public event EventHandler<DirNode>? NodeActivated;

    public event EventHandler<TreemapHit?>? HoverChanged;

    public TreemapHit? HoveredItem => _hover;

    /// <summary>Returns the deepest rectangle at <paramref name="point"/>.</summary>
    public TreemapHit? HitTestAt(Point point)
    {
        // Children are added after their parents, so search from the end.
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Bounds.Contains(point))
                return _hits[i];
        return null;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        _hits.Clear();
        var bounds = new Rect(RenderSize);
        var dc = drawingContext;
        dc.DrawRectangle(Background, null, bounds);

        var root = Root;
        if (root is null || root.SizeBy(Metric) <= 0 || bounds.Width < 1 || bounds.Height < 1) return;

        DrawLevel(dc, root, bounds, 0, 0, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        if (_hover is not null)
        {
            // Hover may refer to the previous layout; look it up again by node.
            _hover = _hits.LastOrDefault(h => h.Node == _hover.Node && h.IsFiles == _hover.IsFiles);
            if (_hover is not null)
                dc.DrawRectangle(null, HoverPen, Deflate(_hover.Bounds, 1));
        }
    }

    private void DrawLevel(DrawingContext dc, DirNode parent, Rect area, int depth, int colorIndex, double pixelsPerDip)
    {
        var items = new List<(DirNode Node, bool IsFiles, long Size)>(parent.Children.Count + 1);
        var metric = Metric;
        foreach (var child in parent.Children)
            if (child.SizeBy(metric) > 0) items.Add((child, false, child.SizeBy(metric)));
        if (parent.OwnFilesSizeBy(metric) > 0)
            items.Add((parent, true, parent.OwnFilesSizeBy(metric)));
        if (items.Count == 0) return;
        items.Sort((a, b) => b.Size.CompareTo(a.Size));

        double total = items.Sum(i => (double)i.Size);
        double scale = area.Width * area.Height / total;
        var areas = items.Select(i => i.Size * scale).ToArray();
        var rects = Squarify(areas, area);

        for (int k = 0; k < items.Count; k++)
        {
            var r = rects[k];
            if (r.Width < MinBlock || r.Height < MinBlock) continue;

            var (node, isFiles, size) = items[k];
            int ci = depth == 0 ? k % Palette.Length : colorIndex;
            dc.DrawRectangle(isFiles ? GetBrush(-1, depth) : GetBrush(ci, depth), BorderPen, r);
            _hits.Add(new TreemapHit(node, isFiles, r, depth));

            if (r.Width > 36 && r.Height > LabelHeight + 2)
            {
                string label = (isFiles ? Loc.T("FilesBlock") : node.Name) + "  " + Format.Bytes(size);
                DrawLabel(dc, label, r, pixelsPerDip, depth);
            }

            if (!isFiles && depth + 1 < MaxDepth && r.Width > 24 && r.Height > LabelHeight + 14)
            {
                var inner = new Rect(r.X + 3, r.Y + LabelHeight, r.Width - 6, r.Height - LabelHeight - 3);
                DrawLevel(dc, node, inner, depth + 1, ci, pixelsPerDip);
            }
        }
    }

    private static void DrawLabel(DrawingContext dc, string text, Rect r, double pixelsPerDip, int depth)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFont,
            11.5, depth == 0 ? Brushes.White : Brushes.Black, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, r.Width - 6),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(r.X + 3, r.Y + 1));
    }

    /// <summary>
    /// Squarified treemap layout (Bruls, Huizing, van Wijk). <paramref name="areas"/> must be sorted
    /// descending and sum to the area of <paramref name="bounds"/>.
    /// </summary>
    internal static Rect[] Squarify(double[] areas, Rect bounds)
    {
        var result = new Rect[areas.Length];
        var r = bounds;
        int i = 0;
        while (i < areas.Length)
        {
            double side = Math.Min(r.Width, r.Height);
            if (side < 0.5) break; // remaining items are too small to draw

            // Grow the row while the worst aspect ratio keeps improving.
            int j = i;
            double sum = 0, worst = double.MaxValue;
            while (j < areas.Length)
            {
                double s = sum + areas[j];
                double w = Worst(areas[i], areas[j], s, side);
                if (j > i && w > worst) break;
                worst = w;
                sum = s;
                j++;
            }

            double thickness = sum / side;
            if (r.Width >= r.Height)
            {
                double y = r.Y;
                for (int k = i; k < j; k++)
                {
                    double h = areas[k] / thickness;
                    result[k] = new Rect(r.X, y, thickness, h);
                    y += h;
                }
                r = new Rect(r.X + thickness, r.Y, Math.Max(0, r.Width - thickness), r.Height);
            }
            else
            {
                double x = r.X;
                for (int k = i; k < j; k++)
                {
                    double w = areas[k] / thickness;
                    result[k] = new Rect(x, r.Y, w, thickness);
                    x += w;
                }
                r = new Rect(r.X, r.Y + thickness, r.Width, Math.Max(0, r.Height - thickness));
            }
            i = j;
        }
        return result;
    }

    private static double Worst(double max, double min, double sum, double side)
    {
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private Brush GetBrush(int paletteIndex, int depth)
    {
        if (_brushCache.TryGetValue((paletteIndex, depth), out var brush)) return brush;
        var baseColor = paletteIndex < 0 ? FilesColor : Palette[paletteIndex];
        double t = Math.Min(0.75, depth * 0.28); // blend toward white per level
        var c = Color.FromRgb(
            (byte)(baseColor.R + (255 - baseColor.R) * t),
            (byte)(baseColor.G + (255 - baseColor.G) * t),
            (byte)(baseColor.B + (255 - baseColor.B) * t));
        brush = Frozen(new SolidColorBrush(c));
        _brushCache[(paletteIndex, depth)] = brush;
        return brush;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTestAt(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        InvalidateVisual();
        HoverChanged?.Invoke(this, hit);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
        HoverChanged?.Invoke(this, null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var root = Root;
        var hit = HitTestAt(e.GetPosition(this));
        if (root is null || hit is null) return;

        // Walk up from the clicked node to the direct child of the current root: drill down one level.
        var node = hit.Node;
        if (node == root) return; // loose files of the root itself
        while (node.Parent is not null && node.Parent != root)
            node = node.Parent;
        if (node.Parent == root)
            NodeActivated?.Invoke(this, node);
    }

    private static Rect Deflate(Rect r, double d) =>
        r.Width > 2 * d && r.Height > 2 * d ? new Rect(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d) : r;

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
