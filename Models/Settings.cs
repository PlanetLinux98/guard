using System.Collections.ObjectModel;

namespace GuardWui3.Models;

public sealed class Settings
{
    public string Dest = "";                       // any folder: local drive, external disk, or network share
    public string Mode = "Additive";               // Additive | Mirror
    public string ExcludeDirs = "node_modules\r\n$RECYCLE.BIN\r\n.git";
    public string ExcludeFiles = "Thumbs.db\r\ndesktop.ini\r\n.DS_Store";
    public bool ScheduleEnabled = true;
    public string ScheduleTime = "02:00";
    public ObservableCollection<FolderPair> Folders = new();

    // App Inventory tab: where the exported app-list.json is written.
    public string AppListDest = "";

    public static ObservableCollection<FolderPair> DefaultFolders() => new()
    {
        new FolderPair(true, @"%USERPROFILE%\Documents",  "Documents"),
        new FolderPair(true, @"%USERPROFILE%\Videos",     "Videos"),
        new FolderPair(true, @"%USERPROFILE%\Desktop",    "Desktop"),
        new FolderPair(true, @"%USERPROFILE%\Pictures",   "Pictures"),
        new FolderPair(true, @"%USERPROFILE%\Music",      "Music"),
        new FolderPair(true, @"%USERPROFILE%\Favorites",  "Favorites"),
        new FolderPair(true, @"%USERPROFILE%\Contacts",   "Contacts"),
    };
}
