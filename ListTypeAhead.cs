using System;
using System.Collections;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace GuardWui3;

// Win32-style type-ahead (first-letter navigation) for the CheckBox-row
// ItemsControls. ItemsControl has no built-in text search - that is a
// ListView/ListBox (Selector) feature, and ListView was deliberately rejected
// here for screen-reader reasons - so we drive it from a list-level KeyDown
// handler. That is the same code-behind wiring the lists already use for focus
// memory, which keeps the logic out of the DataTemplate and safe under
// NativeAOT trimming. Focusing a row moves focus within the list, so it does
// not trip the GettingFocus re-entry redirect (that only fires on entry from
// outside), and the CheckBox announces its own state to a screen reader as
// usual.
public sealed class ListTypeAhead
{
    private readonly ItemsControl _list;
    private readonly Func<object, string> _text;
    private string _buffer = "";
    private long _lastTick;

    // Matches the Windows shell / WinForms reset window: keystrokes within one
    // second build a prefix, a longer pause starts fresh.
    private const long TimeoutTicks = TimeSpan.TicksPerSecond;

    private ListTypeAhead(ItemsControl list, Func<object, string> text)
    {
        _list = list;
        _text = text;
        list.KeyDown += OnKeyDown;
    }

    // text: pulls the string a keystroke matches against from a bound item
    // (the visible primary-column text, so navigation matches what is shown).
    public static void Attach(ItemsControl list, Func<object, string> text)
        => new ListTypeAhead(list, text);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        // Ctrl/Alt combinations are shortcuts and access keys, not type-ahead.
        if (ModifierDown(VirtualKey.Control) || ModifierDown(VirtualKey.Menu)) return;

        char ch = CharFor(e.Key);
        if (ch == '\0') return;
        if (_list.ItemsSource is not IList items || items.Count == 0) return;

        long now = DateTime.UtcNow.Ticks;
        bool fresh = now - _lastTick > TimeoutTicks;
        _lastTick = now;

        int current = CurrentIndex(items);
        string prefix;
        int start;
        if (fresh || _buffer.Length == 0 || (_buffer.Length == 1 && _buffer[0] == ch))
        {
            // Fresh letter, or the same single letter again: cycle through the
            // items starting with it, beginning at the row after the current one.
            _buffer = ch.ToString();
            prefix = _buffer;
            start = current + 1;
        }
        else
        {
            // A different letter typed in quick succession extends the prefix;
            // the current row may still match, so search from it.
            _buffer += ch;
            prefix = _buffer;
            start = current < 0 ? 0 : current;
        }

        int match = FindMatch(items, prefix, start);
        if (match >= 0)
        {
            FocusRow(match);
            e.Handled = true;
        }
    }

    private int CurrentIndex(IList items)
    {
        if (FocusManager.GetFocusedElement(_list.XamlRoot) is FrameworkElement fe && fe.DataContext is { } dc)
        {
            int idx = items.IndexOf(dc);
            if (idx >= 0) return idx;
        }
        return -1; // nothing focused yet -> a fresh letter starts the cycle at row 0
    }

    private int FindMatch(IList items, string prefix, int start)
    {
        int n = items.Count;
        for (int i = 0; i < n; i++)
        {
            int idx = (start + i) % n;
            if (items[idx] is { } item &&
                _text(item).StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                return idx;
        }
        return -1;
    }

    private void FocusRow(int index)
    {
        if (_list.ContainerFromIndex(index) is not DependencyObject container) return;
        if (FindCheckBox(container) is { } cb)
        {
            cb.StartBringIntoView();
            cb.Focus(FocusState.Keyboard);
        }
    }

    private static CheckBox? FindCheckBox(DependencyObject root)
    {
        if (root is CheckBox cb) return cb;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindCheckBox(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    private static bool ModifierDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static char CharFor(VirtualKey k)
    {
        if (k >= VirtualKey.A && k <= VirtualKey.Z) return (char)('a' + (k - VirtualKey.A));
        if (k >= VirtualKey.Number0 && k <= VirtualKey.Number9) return (char)('0' + (k - VirtualKey.Number0));
        if (k >= VirtualKey.NumberPad0 && k <= VirtualKey.NumberPad9) return (char)('0' + (k - VirtualKey.NumberPad0));
        return '\0';
    }
}
