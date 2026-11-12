using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace ImagePostManager;
internal static class WindowAppearance
{
    internal static Color BackgroundColor(bool light) => light ? Color.FromRgb(236, 237, 240) : Color.FromRgb(17, 17, 19);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    public static bool Apply(Window window, bool light)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return false;
        int dark = light ? 0 : 1;
        int rounded = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
        var color = BackgroundColor(light);
        int background = color.R | (color.G << 8) | (color.B << 16);
        int text = light ? 0x2D2827 : 0xEDE9E9;
        int borderResult = DwmSetWindowAttribute(hwnd, 34, ref background, sizeof(int)); // DWMWA_BORDER_COLOR
        int captionResult = DwmSetWindowAttribute(hwnd, 35, ref background, sizeof(int)); // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR
        return borderResult == 0 && captionResult == 0;
    }
}
