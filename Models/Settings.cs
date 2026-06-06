using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GuardWui3.Models;

public sealed class Settings
{
    public string Dest = "";                       // any folder: local drive, external disk, or network share
    public string Mode = "Additive";               // Additive | Mirror
    public string ExcludeDirs = "node_modules\r\n$RECYCLE.BIN\r\n.git";
    public string ExcludeFiles = "Thumbs.db\r\ndesktop.ini\r\n.DS_Store";
    // Off by default: a fresh install should not register a scheduled task until
    // the user explicitly opts in.
    public bool ScheduleEnabled = false;
    public string ScheduleTime = "02:00";
    // Which weekdays the scheduled backup runs on. All seven == daily, one ==
    // weekly, any mix == custom. Defaults to all seven so that enabling the
    // schedule (and any legacy ini without a Days key) behaves like a daily run.
    public List<DayOfWeek> ScheduleDays = AllDays();
    public ObservableCollection<FolderPair> Folders = new();

    // App Inventory tab: where the exported app-list.json is written.
    public string AppListDest = "";

    public static List<DayOfWeek> AllDays() => new()
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

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
