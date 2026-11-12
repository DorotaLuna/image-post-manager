using Microsoft.Win32;
using System.IO;
using System.Security;

namespace ImagePostManager;

internal static class SystemTheme
{
    // Read only at startup or when the user selects System; no timer or subscription.
    internal static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { return false; }
    }
    internal static bool Resolve(string mode, bool systemLight) => mode == "light" || (mode == "system" && systemLight);
}
