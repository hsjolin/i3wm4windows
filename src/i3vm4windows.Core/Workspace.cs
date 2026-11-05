namespace i3vm4windows.Core;

/// <summary>
/// A workspace holds a layout tree, remembers which window has focus and
/// which output (display) it is currently bound to.
/// </summary>
public sealed class Workspace
{
    public int Index { get; }

    public string Name { get; }

    /// <summary>The output this workspace is currently shown on.</summary>
    public string OutputName { get; set; }

    /// <summary>Root container for all windows in this workspace.</summary>
    public SplitNode Root { get; }

    private long? _focusedWindowHandle;
    private readonly List<long> _focusHistory = new();

    /// <summary>
    /// The focused window. Assigning also records the handle in the
    /// focus history, most recent last — the basis for moving focus to
    /// the previously used window when this one closes.
    /// </summary>
    public long? FocusedWindowHandle
    {
        get => _focusedWindowHandle;
        set
        {
            _focusedWindowHandle = value;
            if (value is { } handle)
            {
                _focusHistory.Remove(handle);
                _focusHistory.Add(handle);
                if (_focusHistory.Count > 32)
                {
                    _focusHistory.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>
    /// The window to focus when <paramref name="removed"/> goes away:
    /// the most recently focused of the remaining ones, like a
    /// browser's back button — falling back to the first in layout order.
    /// </summary>
    public long? NextFocusAfter(long removed)
    {
        var handles = WindowHandles;
        for (var i = _focusHistory.Count - 1; i >= 0; i--)
        {
            var candidate = _focusHistory[i];
            if (candidate != removed && handles.Contains(candidate))
            {
                return candidate;
            }
        }

        return handles.Count > 0 ? handles[0] : null;
    }

    public Workspace(int index, string outputName, LayoutMode defaultLayoutMode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        Index = index;
        Name = index.ToString();
        OutputName = outputName;
        Root = new (SplitOrientation.Horizontal, defaultLayoutMode);
    }

    /// <summary>All window handles in the tree, in layout order.</summary>
    public IReadOnlyList<long> WindowHandles => Root.Windows()
        .Select(w => w.WindowHandle)
        .ToList();
}
