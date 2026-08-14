using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GuardWui3.Models;
using GuardWui3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GuardWui3.Views;

// Picks WHAT to restore and WHERE to. The restore itself runs on the File
// Backup page afterwards: it is a long, stoppable job with streamed output, and
// a modal dialog would lock the window for its whole duration and duplicate the
// page's progress and announcement handling.
public sealed partial class RestoreDialog : ContentDialog
{
    public nint WindowHandle { get; set; }

    public ObservableCollection<RestoreItem> Items { get; } = new();
    public string HeaderText { get; private set; } = "";

    private readonly List<BackupSnapshot> _snapshots;
    private readonly List<FolderPair> _folders;
    private readonly string _dest;
    private RestoreItem? _current;
    // The row element itself, not just its item: focus is sent back to it after
    // a redirect (so the changed row is read out) and on re-entry to the list.
    private FrameworkElement? _currentElement;
    private bool _loading;

    public BackupSnapshot Snapshot => _snapshots[Math.Clamp(CmbSnapshot.SelectedIndex, 0, _snapshots.Count - 1)];

    public RestoreMode Mode => RbReplace.IsChecked == true ? RestoreMode.Replace : RestoreMode.AddAndUpdate;

    // Only the rows the user actually ticked, in list order.
    public List<RestoreItem> Picked
    {
        get
        {
            var picked = new List<RestoreItem>();
            foreach (var i in Items) if (i.Include) picked.Add(i);
            return picked;
        }
    }

    public RestoreDialog(string dest, List<BackupSnapshot> snapshots, IEnumerable<FolderPair> folders)
    {
        InitializeComponent();
        _dest = dest;
        _snapshots = snapshots;
        _folders = new List<FolderPair>(folders);

        HeaderText = "Copies files from your backup back into the folders they came from."
            + " Tick what you want back, check where each folder is going, then choose Restore.";

        _loading = true;
        foreach (var s in _snapshots)
            CmbSnapshot.Items.Add(new ComboBoxItem { Content = s.Label });
        CmbSnapshot.SelectedIndex = 0;
        // One backup is the normal case (versioning is off by default), and a
        // picker with a single entry is a control that only wastes a tab stop.
        SnapshotRow.Visibility = _snapshots.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
        LoadCandidates();

        // Tracked at the list level, not in the DataTemplate: a handler
        // referenced from inside a DataTemplate is not reliably resolvable and
        // gets trimmed under NativeAOT (see the main window's folder list).
        ItemList.GotFocus += OnItemGotFocus;
        // TabFocusNavigation="Once" always re-enters the list at the FIRST row,
        // which matters more here than on the main page: Change Restore Location
        // acts on the row that last held focus, so without this every redirect
        // costs the user their place and they have to arrow back down to it.
        ItemList.GettingFocus += OnItemGettingFocus;
    }

    private void LoadCandidates()
    {
        Items.Clear();
        _current = null;
        foreach (var c in RestorePlan.BuildCandidates(Snapshot.Path, _folders))
            Items.Add(new RestoreItem(c));
    }

    private void OnSnapshotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LoadCandidates();
    }

    private void OnItemGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is RestoreItem r)
        {
            _current = r;
            _currentElement = fe;
        }
    }

    private void OnItemGettingFocus(UIElement sender, GettingFocusEventArgs args)
        => ListFocus.Restore(ItemList, _currentElement, args);

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        // Only rows that have somewhere to go: ticking one without a restore
        // location just moves the refusal to the Restore button.
        foreach (var i in Items) if (i.Target.Length > 0) i.Include = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var i in Items) i.Include = false;
    }

    private async void OnChangeTarget(object sender, RoutedEventArgs e)
    {
        var row = _current;
        if (row == null)
        {
            await UiHelpers.ShowNestedMessageAsync(this,
                "Highlight the folder you want to redirect in the list, then press Change Restore Location.");
            return;
        }
        Windows.Storage.StorageFolder? folder;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
            picker.FileTypeFilter.Add("*");
            folder = await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            // The WinRT picker can throw in an unpackaged app; that must fail the
            // browse, not the app (mirrors FolderDialog).
            await UiHelpers.ShowNestedMessageAsync(this, "Could not open the folder picker:\n\n" + ex.Message);
            return;
        }
        if (folder == null) return;
        string? problem = RestorePlan.ValidateTarget(folder.Path, _dest, GuardPaths.BaseDir, GuardPaths.DataDir);
        if (problem != null)
        {
            await UiHelpers.ShowNestedMessageAsync(this, problem);
            return;
        }
        row.Target = folder.Path;
        // Redirecting a row is only ever done in order to restore it.
        row.Include = true;
        // Focus back on the row so its rewritten Caption is read out: otherwise
        // focus stays on the button and the only confirmation the redirect took
        // is a change to a line the user is not looking at.
        _currentElement?.Focus(FocusState.Programmatic);
    }

    private void OnToggleAccessKeyInvoked(UIElement sender, AccessKeyInvokedEventArgs args)
    {
        // WinUI's Alt handling toggles without moving focus first, so a screen
        // reader has nothing focused to read; the main window does the same.
        if (sender is Control control) control.Focus(FocusState.Keyboard);
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? problem = Validate();
        if (problem == null) return;
        var deferral = args.GetDeferral();
        args.Cancel = true;
        await UiHelpers.ShowNestedMessageAsync(this, problem);
        deferral.Complete();
    }

    // Every refusal here is final rather than advisory: a restore writes into
    // live folders, so there is no run-time skip to fall back on and no second
    // chance once files have been written over.
    private string? Validate()
    {
        var picked = Picked;
        if (picked.Count == 0)
            return Items.Count == 0
                ? "This backup holds no folders to restore."
                : "Tick at least one folder to restore.";
        foreach (var r in picked)
        {
            string? problem = RestorePlan.ValidateTarget(r.Target, _dest, GuardPaths.BaseDir, GuardPaths.DataDir);
            if (problem != null)
                return "\"" + r.FolderName + "\" cannot be restored yet.\n\n" + problem
                    + "\n\nHighlight that row and press Change Restore Location.";
        }
        // Two rows aimed at one folder would each copy over the other, and the
        // result depends on which ran last - not something to discover
        // afterwards. Nesting counts as the same folder: restoring into
        // Documents writes its whole tree, including a Documents\Reports the
        // next row is about to write into as well.
        for (int i = 0; i < picked.Count; i++)
            for (int j = i + 1; j < picked.Count; j++)
                if (Overlaps(picked[i].Target, picked[j].Target))
                    return "\"" + picked[i].FolderName + "\" and \"" + picked[j].FolderName
                        + "\" are set to restore into the same folder, or into one inside the other:\n\n"
                        + picked[i].Target + "\n" + picked[j].Target
                        + "\n\nGive each one its own separate folder, or untick one of them.";
        return null;
    }

    // Whether two restore targets are the same folder or one contains the
    // other. Both keys end in a separator, so the prefix test only matches whole
    // path segments (C:\Foo does not contain C:\Foobar).
    private static bool Overlaps(string a, string b)
    {
        string ka = Key(a), kb = Key(b);
        if (ka.Length == 0 || kb.Length == 0) return false;
        return ka.StartsWith(kb, StringComparison.OrdinalIgnoreCase)
            || kb.StartsWith(ka, StringComparison.OrdinalIgnoreCase);
    }

    private static string Key(string? raw)
    {
        string p = System.Environment.ExpandEnvironmentVariables((raw ?? "").Trim());
        if (p.Length == 0) return "";
        try { p = System.IO.Path.GetFullPath(p); }
        catch { return ""; }
        return p.EndsWith(System.IO.Path.DirectorySeparatorChar) ? p : p + System.IO.Path.DirectorySeparatorChar;
    }
}
