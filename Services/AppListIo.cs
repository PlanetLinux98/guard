using System.IO;
using System.Text.Json;
using GuardWui3.Models;

namespace GuardWui3.Services;

public static class AppListIo
{
    public static void Write(string path, AppListFile file)
    {
        // Atomic like every other durable artifact: a pulled USB drive or lost
        // power mid-write must not leave a truncated app-list.json that later
        // silently fails to import.
        string json = JsonSerializer.Serialize(file, GuardJsonContext.Default.AppListFile);
        AtomicFile.WriteAllText(path, json);
    }

    public static AppListFile? Read(string path)
    {
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize(fs, GuardJsonContext.Default.AppListFile);
    }
}
