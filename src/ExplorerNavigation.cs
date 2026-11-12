using System.Diagnostics;
using System.IO;

namespace ImagePostManager;

internal static class ExplorerNavigation
{
    internal static ProcessStartInfo CreateStartInfo(string root, Post? post = null)
    {
        var folder = Path.GetFullPath(post == null ? root : Path.Combine(root, "originals"));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("路径不可用，请确认文件夹存在且磁盘已连接。");
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        var photo = post?.Images.FirstOrDefault(p => p.Id == post.Cover) ?? post?.Images.FirstOrDefault();
        if (photo != null && Guid.TryParseExact(photo.Id, "N", out _) && photo.Extension is ".png" or ".jpg" or ".jpeg")
        {
            var file = Path.Combine(folder, photo.Id + photo.Extension);
            if (File.Exists(file)) { start.Arguments = "/select,\"" + file + "\""; return start; }
        }
        start.ArgumentList.Add(folder);
        return start;
    }

    internal static void Open(string root, Post? post = null) => Process.Start(CreateStartInfo(root, post));
}
