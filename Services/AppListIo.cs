using System.IO;
using System.Text.Json;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class AppListIo
{
    public static void Write(string path, AppListFile file)
    {
        using var fs = File.Create(path);
        JsonSerializer.Serialize(fs, file, GuardJsonContext.Default.AppListFile);
    }

    public static AppListFile? Read(string path)
    {
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize(fs, GuardJsonContext.Default.AppListFile);
    }
}
