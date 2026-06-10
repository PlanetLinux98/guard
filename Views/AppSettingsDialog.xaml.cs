using System.Collections.Generic;
using System.Collections.ObjectModel;
using GuardWui3.Models;
using Microsoft.UI.Xaml.Controls;

namespace GuardWui3.Views;

// Confirmation step for Export App Settings: the folder matching is heuristic,
// so nothing is copied until the user has seen (and could untick) every match.
public sealed partial class AppSettingsDialog : ContentDialog
{
    // Bound by x:Bind in the XAML.
    public ObservableCollection<AppSettingsCandidate> Candidates { get; } = new();

    public AppSettingsDialog(IEnumerable<AppSettingsCandidate> candidates)
    {
        foreach (var c in candidates) Candidates.Add(c);
        InitializeComponent();
    }
}
