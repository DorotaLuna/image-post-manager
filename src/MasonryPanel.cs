using System.Windows;
using System.Windows.Controls;
namespace ImagePostManager;
public sealed class MasonryPanel : Panel
{
    public const double Gap = 16;
    public const double DefaultTileWidth = 236;
    /// <summary>Column step: the target card width plus the gap. Larger values mean fewer, bigger images.</summary>
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(DefaultTileWidth, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }
    /// <summary>Columns produced by the most recent measure pass; used for verification.</summary>
    public int ColumnCount { get; private set; }
    private List<Rect> positions = [];
    private IReadOnlyList<string>? keys;
    private Func<int, UIElement>? factory;
    private Func<int, double>? ratio;
    private readonly Dictionary<int, UIElement> realized = [];
    private double viewportTop, viewportHeight = 800;
    private string[] layoutKeys = [];
    internal string? IgnoreAnchorKey { get; set; }
    internal event Action<double>? AnchorShift;
    public bool IsVirtual => keys != null;
    internal IReadOnlyList<Rect> Positions => positions;
    internal int IndexOf(UIElement child) => IsVirtual ? realized.First(pair => pair.Value == child).Key : Children.IndexOf(child);
    internal void SetVirtual(IReadOnlyList<string>? items, Func<int, UIElement>? create = null, Func<int, double>? aspect = null)
    {
        if (items != null && keys != null)
        {
            var byId = realized.ToDictionary(pair => keys[pair.Key], pair => pair.Value);
            realized.Clear();
            for (int i = 0; i < items.Count; i++) if (byId.Remove(items[i], out var child)) realized[i] = child;
            foreach (var child in byId.Values) Children.Remove(child);
        }
        else { realized.Clear(); Children.Clear(); layoutKeys = []; }
        keys = items; factory = create; ratio = aspect; InvalidateMeasure();
    }
    internal void SetViewport(double top, double height)
    {
        if (Math.Abs(top - viewportTop) < 1 && Math.Abs(height - viewportHeight) < 1) return;
        viewportTop = top; viewportHeight = Math.Max(1, height); if (IsVirtual) InvalidateMeasure();
    }
    internal int DropIndex(Point point)
    {
        int best = 0; double distance = double.PositiveInfinity;
        for (int i = 0; i < positions.Count; i++)
        {
            var r = positions[i]; double dx = point.X - (r.X + r.Width / 2), dy = point.Y - (r.Y + r.Height / 2);
            double d = dx * dx + dy * dy;
            if (d < distance) { distance = d; best = i + (dx / r.Width + dy / r.Height < 0 ? 0 : 1); }
        }
        return Math.Clamp(best, 0, positions.Count);
    }
    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 900 : Math.Max(1, available.Width);
        double tile = double.IsFinite(TileWidth) && TileWidth > Gap ? TileWidth : DefaultTileWidth;
        int columns = Math.Max(1, (int)((width + Gap) / tile));
        ColumnCount = columns;
        double columnWidth = Math.Max(1, (width - Gap * (columns - 1)) / columns);
        string? anchor = null; double anchorTop = 0;
        if (keys != null && positions.Count == layoutKeys.Length)
        {
            for (int i = 0; i < positions.Count; i++) if (positions[i].Bottom >= viewportTop && layoutKeys[i] != IgnoreAnchorKey)
            { anchor = layoutKeys[i]; anchorTop = positions[i].Top; break; }
        }
        double[] heights = new double[columns]; positions = [];
        if (keys != null)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                int column = Array.IndexOf(heights, heights.Min());
                double h = Math.Max(1, columnWidth - 2) / Math.Clamp(ratio!(i), .05, 20) + 2;
                positions.Add(new Rect(column * (columnWidth + Gap), heights[column], columnWidth, h));
                heights[column] += h + Gap;
            }
            layoutKeys = keys.ToArray(); IgnoreAnchorKey = null;
            if (anchor != null)
            {
                int newIndex = Array.IndexOf(layoutKeys, anchor);
                if (newIndex >= 0 && Math.Abs(positions[newIndex].Top - anchorTop) > 1)
                {
                    double offset = Math.Max(0, viewportTop + positions[newIndex].Top - anchorTop);
                    viewportTop = offset; AnchorShift?.Invoke(offset);
                }
            }
            var visible = Enumerable.Range(0, positions.Count).Where(i => positions[i].Bottom >= viewportTop - 500 && positions[i].Top <= viewportTop + viewportHeight + 500).ToHashSet();
            foreach (var i in realized.Keys.Where(i => !visible.Contains(i)).ToArray()) { Children.Remove(realized[i]); realized.Remove(i); }
            foreach (var i in visible.Order())
            {
                if (!realized.TryGetValue(i, out var child)) { child = factory!(i); realized.Add(i, child); Children.Add(child); }
                child.Measure(new Size(columnWidth, positions[i].Height));
            }
            var ordered = realized.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
            for (int i = 0; i < ordered.Length; i++)
                if (!ReferenceEquals(Children[i], ordered[i])) { Children.Remove(ordered[i]); Children.Insert(i, ordered[i]); }
            return new Size(width, Math.Max(0, heights.Max() - Gap));
        }
        foreach (UIElement child in InternalChildren)
        {
            int column = Array.IndexOf(heights, heights.Min());
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            positions.Add(new Rect(column * (columnWidth + Gap), heights[column], columnWidth, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Gap;
        }
        return new Size(width, Math.Max(0, heights.Max() - Gap));
    }
    protected override Size ArrangeOverride(Size size)
    {
        if (IsVirtual) { foreach (var pair in realized) pair.Value.Arrange(positions[pair.Key]); return size; }
        for (int i = 0; i < InternalChildren.Count && i < positions.Count; i++) InternalChildren[i].Arrange(positions[i]);
        return size;
    }
}
