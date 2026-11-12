using System.IO;
using System.Windows;
namespace ImagePostManager;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            int index = Array.IndexOf(args, "--self-test");
            if (index + 1 >= args.Length) return 2;
            return SelfTest.Run(args[index + 1], index + 2 < args.Length ? args[index + 2] : null);
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        { MessageBox.Show(e.Exception.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); e.Handled = true; };
        var directory = Preferences.PortableDirectory;
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImagePostManager");
            Preferences.MigrateLegacy(directory, legacy);
            var preferences = Preferences.Load(directory);
            preferences.Save(); // Materialize settings.json beside the executable and confirm the folder is writable.
            app.Run(new MainWindow(null, preferences));
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + $"\n\n程序按便携方式运行，设置保存在程序目录：\n{directory}", "无法启动 Image Post Manager");
            return 1;
        }
    }
}
