using System.Windows;
using System.Windows.Controls;

namespace ImagePostManager;

internal sealed class AspectSurface : Grid
{
    internal double Ratio { get; set; } = 1;
    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsFinite(available.Width) ? available.Width : 236;
        var size = new Size(width, width / Math.Clamp(Ratio, .05, 20));
        base.MeasureOverride(size);
        return size;
    }
}
