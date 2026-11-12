using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace ImagePostManager;
public sealed class Preferences
{
    // Portable mode: settings ship next to the executable instead of %LOCALAPPDATA%.
    public const double MinimumCardWidth = 140;
    public const double MaximumCardWidth = 420;
    public const double DefaultCardWidth = 236;
    public List<string> Libraries { get; set; } = [];
    public string Theme { get; set; } = "dark";
    public double CardWidth { get; set; } = DefaultCardWidth;
    [JsonIgnore] public string? FilePath { get; set; }
    public static string PortableDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static string PortableFile(string directory) => Path.Combine(directory, "settings.json");
    public static Preferences Load(string directory)
    {
        var file = Path.Combine(directory, "settings.json");
        var settings = File.Exists(file) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file)) ?? throw new InvalidDataException("设置为空") : new Preferences();
        settings.FilePath = file;
        if (!File.Exists(file))
        {
            var legacy = Path.Combine(directory, "library.txt");
            if (File.Exists(legacy) && !string.IsNullOrWhiteSpace(File.ReadAllText(legacy))) settings.Add(File.ReadAllText(legacy).Trim());
        }
        settings.Libraries = settings.Libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.CardWidth = !double.IsFinite(settings.CardWidth) || settings.CardWidth <= 0
            ? DefaultCardWidth
            : Math.Clamp(settings.CardWidth, MinimumCardWidth, MaximumCardWidth);
        return settings;
    }
    /// <summary>
    /// One-time move of a settings store written by an older build (usually under %LOCALAPPDATA%)
    /// into the portable program folder. The old file is removed only after the new copy exists,
    /// and an existing portable settings.json is never overwritten.
    /// </summary>
    public static bool MigrateLegacy(string directory, string legacyDirectory)
    {
        if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(legacyDirectory), StringComparison.OrdinalIgnoreCase)) return false;
        var target = PortableFile(directory);
        if (File.Exists(target)) return false;
        var legacySettings = Path.Combine(legacyDirectory, "settings.json");
        var legacyList = Path.Combine(legacyDirectory, "library.txt");
        bool hasSettings = File.Exists(legacySettings), hasList = File.Exists(legacyList);
        if (!hasSettings && !hasList) return false;
        Directory.CreateDirectory(directory);
        if (hasSettings) File.Copy(legacySettings, target, true);
        else
        {
            var migrated = new Preferences { FilePath = target };
            var text = File.ReadAllText(legacyList).Trim();
            if (text.Length > 0) migrated.Add(text); else migrated.Save();
        }
        if (hasSettings) TryDelete(legacySettings);
        if (hasList) TryDelete(legacyList);
        TryRemoveEmptyDirectory(legacyDirectory);
        return true;
    }
    private static void TryDelete(string file)
    { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void TryRemoveEmptyDirectory(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Add(string path)
    {
        path = Path.GetFullPath(path);
        if (!Libraries.Contains(path, StringComparer.OrdinalIgnoreCase)) Libraries.Add(path);
        Save();
    }
    public void Remove(string path)
    {
        var old = Libraries.ToList();
        Libraries.RemoveAll(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        try { Save(); } catch { Libraries = old; throw; }
    }
    public void Save()
    {
        if (FilePath == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, this); stream.Flush(true); }
        File.Move(temp, FilePath, true);
    }
}
