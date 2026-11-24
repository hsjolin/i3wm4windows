using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using i3vm4windows.App.Interop;
using i3vm4windows.Config;

namespace i3vm4windows.App.Services;

/// <summary>One tab of the bar: the window's title, or whether it is the
/// active one.</summary>
public sealed record BarTab(string Title, bool Active);

/// <summary>
/// An i3bar-style strip along the top (or bottom) of every display: the
/// bound workspace numbers with the active one highlighted, every tab's
/// title when the workspace shows tabs, and the configured status items
/// — print, exec, cpu, ram, battery and padding — along the right edge.
/// The tiling keeps its distance: the manager takes the strip off the
/// work area, and this window covers the freed strip, pinned above
/// everything.
/// </summary>
internal sealed class WorkspaceBar : IDisposable
{
    /// <summary>How often the status items refresh.</summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(2);

    /// <summary>The widest a tab's title chip may become; longer titles
    /// end in an ellipsis.</summary>
    private const double TabMaxWidth = 180;

    /// <summary>The status font size, also used to measure text.</summary>
    private readonly System.Windows.Media.FontFamily _fontFamily;
    private readonly double _fontSize;

    private readonly BarConfig _config;
    private readonly DispatcherTimer _statusTimer;
    private readonly Dictionary<string, Bar> _bars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Widest cell each status slot has shown so far, so one
    /// item growing never moves the others (soft padding).</summary>
    private readonly List<double> _slotWidths = new();

    private double _charWidth;
    private (ulong Idle, ulong Total)? _lastCpu;
    private bool _statusRunning;
    private bool _disposed;

    /// <summary>One strip per display.</summary>
    private sealed class Bar : IDisposable
    {
        public required Window Window { get; init; }
        public required StackPanel Chips { get; init; }
        public required StackPanel Tabs { get; init; }
        public required StackPanel Status { get; init; }

        public void Dispose() => Window.Close();
    }

    public WorkspaceBar(BarConfig config)
    {
        _config = config;
        _fontFamily = TryFontFamily(config.BarFont);
        _fontSize = config.FontSize;
        if (config.Clock || config.Date || config.BarItems.Count > 0)
        {
            _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = StatusInterval,
            };
            _statusTimer.Tick += (_, _) => UpdateStatus();
            _statusTimer.Start();
            UpdateStatus();
        }
        else
        {
            _statusTimer = null!;
        }
    }

    /// <summary>Brings the display's strip in sync with the workspaces.</summary>
    public void Update(
        string output,
        i3vm4windows.Core.Rect workArea,
        IReadOnlyList<int> workspaces,
        int activeIndex,
        IReadOnlyList<BarTab> tabs)
    {
        if (_disposed)
        {
            return;
        }

        var bar = EnsureBar(output);
        BuildChips(bar.Chips, workspaces, activeIndex);
        BuildTabs(bar.Tabs, tabs);
        Position(bar.Window, workArea);
    }

    /// <summary>Closes the strips of displays that are gone.</summary>
    public void CloseMissing(IReadOnlyCollection<string> outputs)
    {
        var present = new HashSet<string>(outputs, StringComparer.OrdinalIgnoreCase);
        foreach (var output in _bars.Keys.Where(o => !present.Contains(o)).ToList())
        {
            _bars[output].Dispose();
            _bars.Remove(output);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer?.Stop();
        foreach (var bar in _bars.Values)
        {
            bar.Dispose();
        }

        _bars.Clear();
    }

    /// <summary>
    /// Refreshes the status items and shows them on every display's
    /// strip. Exec commands run in parallel off the dispatcher; a slow
    /// one delays its own tick, never the strip.
    /// </summary>
    private async void UpdateStatus()
    {
        if (_statusRunning)
        {
            return;
        }

        _statusRunning = true;
        try
        {
            var items = new List<BarItem>();
            if (_config.Clock)
            {
                items.Add(new BarItem(BarItemKind.Print, DateTime.Now.ToString("HH:mm")));
            }

            if (_config.Date)
            {
                items.Add(new BarItem(BarItemKind.Print, DateTime.Now.ToString("yyyy-MM-dd")));
            }

            items.AddRange(_config.BarItems);

            // One value per item index; commands run in parallel.
            var values = new Dictionary<int, string>(items.Count);
            var commandTasks = new List<(int Index, Task<string> Output)>();
            for (var i = 0; i < items.Count; i++)
            {
                switch (items[i].Kind)
                {
                    case BarItemKind.Cpu:
                        values[i] = CpuPercent()?.ToString() ?? "";
                        break;
                    case BarItemKind.Ram:
                        values[i] = MemoryPercent()?.ToString() ?? "";
                        break;
                    case BarItemKind.Battery:
                        values[i] = BatteryPercent()?.ToString() ?? "";
                        break;
                    case BarItemKind.Command:
                        commandTasks.Add((i, RunCommand(items[i].Text)));
                        break;
                    default:
                        values[i] = items[i].Text;
                        break;
                }
            }

            foreach (var (index, output) in commandTasks)
            {
                values[index] = await output;
            }

            foreach (var bar in _bars.Values)
            {
                BuildStatus(bar.Status, items, values);
            }
        }
        finally
        {
            _statusRunning = false;
        }
    }

    /// <summary>
    /// The busy percentage between the previous sample and now, or null
    /// on the first sample: kernel time includes idle, so busy is
    /// kernel + user - idle.
    /// </summary>
    private int? CpuPercent()
    {
        if (!Win32.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var idleTicks = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
        var kernelTicks = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
        var userTicks = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;
        var total = kernelTicks + userTicks;
        var previous = _lastCpu;
        _lastCpu = (idleTicks, total);
        if (previous is not { } before || total <= before.Total)
        {
            return null;
        }

        var busy = (total - before.Total) - (idleTicks - before.Idle);
        return Math.Clamp((int)(100 * busy / (total - before.Total)), 0, 100);
    }

    private static int? MemoryPercent()
    {
        var status = new Win32.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>(),
        };
        return Win32.GlobalMemoryStatusEx(ref status) ? (int)status.dwMemoryLoad : null;
    }

    /// <summary>Battery charge 0-100, or null when unknown (255).</summary>
    private static int? BatteryPercent()
    {
        return Win32.GetSystemPowerStatus(out var status) && status.BatteryLifePercent <= 100
            ? status.BatteryLifePercent
            : null;
    }

    /// <summary>
    /// Runs one status command through cmd.exe, so pipes and scripts
    /// work, and returns its first line of output.
    /// </summary>
    private static async Task<string> RunCommand(string command)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return "";
            }

            return (await process.StandardOutput.ReadLineAsync() ?? "").Trim();
        }
        catch (Exception error)
        {
            AppLog.Warn($"bar command failed ({command}): {error.Message}");
            return "";
        }
    }

    private Bar EnsureBar(string output)
    {
        if (_bars.TryGetValue(output, out var bar))
        {
            return bar;
        }

        var chips = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        var tabs = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        var status = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        var content = new DockPanel
        {
            Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(235, 24, 26, 32)),
        };
        DockPanel.SetDock(chips, Dock.Left);
        DockPanel.SetDock(status, Dock.Right);
        content.Children.Add(chips);
        content.Children.Add(status);
        content.Children.Add(tabs);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Content = content,
        };

        // Tool window so the shell and Alt+Tab ignore it, no-activate so
        // it never steals focus.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var extended = Win32.GetWindowLongPtr(handle, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(
                handle, Win32.GWL_EXSTYLE,
                extended | (nint)Win32.WS_EX_TOOLWINDOW | (nint)Win32.WS_EX_NOACTIVATE);
        };

        _bars[output] = bar = new Bar
        {
            Window = window,
            Chips = chips,
            Tabs = tabs,
            Status = status,
        };
        return bar;
    }

    /// <summary>
    /// Covers the strip the manager freed at the top (or bottom) of the
    /// work area; both are in physical pixels, so the positioning goes
    /// through Win32, never through the DIP-space of the WPF window.
    /// </summary>
    private void Position(Window window, i3vm4windows.Core.Rect workArea)
    {
        window.UpdateLayout();
        if (!window.IsVisible)
        {
            // Park off-screen until the position is known.
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            window.UpdateLayout();
        }

        var handle = new WindowInteropHelper(window).Handle;
        var top = _config.Position == "bottom"
            ? workArea.Bottom
            : workArea.Y - _config.Height;
        Win32.SetWindowPos(
            handle, IntPtr.Zero,
            workArea.X, top, workArea.Width, _config.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private void BuildChips(StackPanel chips, IReadOnlyList<int> workspaces, int activeIndex)
    {
        chips.Children.Clear();
        foreach (var index in workspaces)
        {
            var active = index == activeIndex;
            var chip = new Border
            {
                MinWidth = 28,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(2, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = active
                    ? System.Windows.Media.Brushes.White
                    : System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = index.ToString(),
                    FontSize = _fontSize,
                    FontFamily = _fontFamily,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = active
                        ? System.Windows.Media.Brushes.Black
                        : System.Windows.Media.Brushes.White,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                },
            };
            chips.Children.Add(chip);
        }
    }

    private void BuildTabs(StackPanel tabs, IReadOnlyList<BarTab> barTabs)
    {
        tabs.Children.Clear();
        foreach (var tab in barTabs)
        {
            tabs.Children.Add(new Border
            {
                MaxWidth = TabMaxWidth,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(2, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = tab.Active
                    ? System.Windows.Media.Brushes.White
                    : System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = tab.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = _fontSize,
                    FontFamily = _fontFamily,
                    Foreground = tab.Active
                        ? System.Windows.Media.Brushes.Black
                        : System.Windows.Media.Brushes.White,
                },
            });
        }
    }

    /// <summary>The width of one character at the bar's font, for
    /// padding spacers.</summary>
    /// <summary>The configured bar font, falling back to Segoe UI with
    /// the emoji fallback when the configured family does not exist.</summary>
    private static System.Windows.Media.FontFamily TryFontFamily(string name)
    {
        try
        {
            return new System.Windows.Media.FontFamily(name);
        }
        catch
        {
            return new System.Windows.Media.FontFamily("Segoe UI, Segoe UI Emoji");
        }
    }

    private double CharWidth => _charWidth > 0 ? _charWidth : _charWidth = Measure("0");

    private double Measure(string text) => new FormattedText(
        text,
        CultureInfo.InvariantCulture,
        System.Windows.FlowDirection.LeftToRight,
        new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        _fontSize,
        System.Windows.Media.Brushes.White,
        1.0).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// Lays the status items out left to right with no margins. Every
    /// item gets a cell as wide as the widest it has ever been, so an
    /// item growing within that width never moves its neighbours — the
    /// padding is soft. padding x items are fixed spacers of x
    /// characters.
    /// </summary>
    private void BuildStatus(
        StackPanel status,
        IReadOnlyList<BarItem> items,
        IReadOnlyDictionary<int, string> values)
    {
        status.Children.Clear();
        while (_slotWidths.Count < items.Count)
        {
            _slotWidths.Add(0);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind == BarItemKind.Padding)
            {
                status.Children.Add(new Border
                {
                    Width = Math.Max(1, item.Padding) * CharWidth,
                    Margin = new Thickness(0),
                });
                continue;
            }

            var text = values.TryGetValue(i, out var value) ? value : "";
            var width = Measure(text);
            if (width > _slotWidths[i])
            {
                _slotWidths[i] = width;
            }

            status.Children.Add(new Border
            {
                Width = _slotWidths[i],
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = _fontSize,
                    FontFamily = _fontFamily,
                    Foreground = System.Windows.Media.Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }
}
