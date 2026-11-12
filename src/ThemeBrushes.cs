using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
namespace ImagePostManager;
internal static class ThemeBrushes
{
    private sealed class Value : INotifyPropertyChanged
    {
        private Color color;
        public Color Color { get => color; set { color = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static readonly Dictionary<SolidColorBrush, Value> Values = [];
    public static SolidColorBrush Create(Color color)
    {
        var value = new Value { Color = color };
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, new Binding(nameof(Value.Color)) { Source = value });
        Values.Add(brush, value); return brush;
    }
    public static void Set(SolidColorBrush brush, Color color) => Values[brush].Color = color;
}
