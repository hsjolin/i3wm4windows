using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using i3vm4windows.App.Interop;
using i3vm4windows.Config;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

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
/// The strip's content is a WebView2 page, not WPF controls: WPF renders
/// emoji monochrome, Chromium renders them in color (Segoe UI Emoji is a
/// COLR font). The host posts the state as JSON; the page renders it.
/// </summary>
internal sealed class WorkspaceBar : IDisposable
{
    /// <summary>How often the status items refresh.</summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(2);

    /// <summary>The widest a tab's title chip may become; longer titles
    /// end in an ellipsis.</summary>
    private const double TabMaxWidth = 180;

    /// <summary>The bar's background, matching the old WPF brush.</summary>
    private static readonly System.Windows.Media.Color BarColor =
        System.Windows.Media.Color.FromArgb(235, 24, 26, 32);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The WebView2 environment is shared by every display's
    /// strip; one browser process pool serves them all.</summary>
    private static readonly Lazy<Task<CoreWebView2Environment>> WebViewEnvironment =
        new(() => CoreWebView2Environment.CreateAsync(
            userDataFolder: System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "i3vm4windows", "webview2")));

    private readonly BarConfig _config;
    private readonly string _fontFamilyCss;
    private readonly double _fontSize;
    private readonly DispatcherTimer _statusTimer;
    private readonly Dictionary<string, Bar> _bars = new(StringComparer.OrdinalIgnoreCase);

    private (ulong Idle, ulong Total)? _lastCpu;
    private bool _statusRunning;
    private bool _disposed;

    /// <summary>One strip per display.</summary>
    private sealed class Bar : IDisposable
    {
        public required Window Window { get; init; }
        public required WebView2 View { get; init; }

        /// <summary>Set once the page has loaded; messages posted before
        /// are lost, so the host buffers the state and sends on ready.</summary>
        public volatile bool Ready;

        /// <summary>WebView2 can only attach once both the window and the
        /// control have their HWNDs — that is after Show + layout, so the
        /// init starts there, exactly once.</summary>
        public volatile bool InitStarted;

        public IReadOnlyList<Chip> Chips = Array.Empty<Chip>();
        public IReadOnlyList<Tab> Tabs = Array.Empty<Tab>();
        public IReadOnlyList<Slot> Status = Array.Empty<Slot>();

        public void Send()
        {
            if (!Ready)
            {
                return;
            }

            var state = new BarState(Chips, Tabs, Status);
            View.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(state, JsonOptions));
        }

        public void Dispose()
        {
            View.Dispose();
            Window.Close();
        }
    }

    private sealed record BarState(
        IReadOnlyList<Chip> Chips,
        IReadOnlyList<Tab> Tabs,
        IReadOnlyList<Slot> Status);

    private sealed record Chip(string Label, bool Active);

    private sealed record Tab(string Title, bool Active);

    /// <summary>One status cell: either text, or a padding spacer of
    /// <see cref="Padding"/> characters.</summary>
    private sealed record Slot(string? Text, int Padding);

    public WorkspaceBar(BarConfig config)
    {
        _config = config;
        _fontSize = config.FontSize;
        // A comma-separated family list becomes a CSS list with each
        // family quoted, so "Segoe UI, Segoe UI Emoji" works in the page.
        _fontFamilyCss = string.Join(", ", config.BarFont
            .Split(',')
            .Select(part => "\"" + part.Trim().Trim('"') + "\""));
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
        bar.Chips = workspaces
            .Select(index => new Chip(index.ToString(CultureInfo.InvariantCulture), index == activeIndex))
            .ToList();
        bar.Tabs = tabs
            .Select(tab => new Tab(tab.Title, tab.Active))
            .ToList();
        bar.Send();
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
                        values[i] = CpuPercent()?.ToString(CultureInfo.InvariantCulture) ?? "";
                        break;
                    case BarItemKind.Ram:
                        values[i] = MemoryPercent()?.ToString(CultureInfo.InvariantCulture) ?? "";
                        break;
                    case BarItemKind.Battery:
                        values[i] = BatteryPercent()?.ToString(CultureInfo.InvariantCulture) ?? "";
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

            var slots = items
                .Select((item, i) => item.Kind == BarItemKind.Padding
                    ? new Slot(null, Math.Max(1, item.Padding))
                    : new Slot(values.TryGetValue(i, out var value) ? value : "", 0))
                .ToList();

            foreach (var bar in _bars.Values)
            {
                bar.Status = slots;
                bar.Send();
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

        var view = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(BarColor.A, BarColor.R, BarColor.G, BarColor.B),
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = true,
            Background = new SolidColorBrush(BarColor),
            Content = view,
        };

        _bars[output] = bar = new Bar
        {
            Window = window,
            View = view,
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

        // WebView2 can only attach once the control itself has its HWND,
        // which happens at Loaded — not at SourceInitialized, and not
        // right after Show in the same dispatcher pass.
        window.Loaded += (_, _) =>
        {
            if (!bar.InitStarted)
            {
                bar.InitStarted = true;
                _ = InitViewAsync(bar);
            }
        };

        return bar;
    }

    private async Task InitViewAsync(Bar bar)
    {
        try
        {
            var environment = await WebViewEnvironment.Value;
            // The control's HWND appears only after its first render; wait
            // for the HwndSource before attaching, or the loader fails with
            // 0x80070578 / E_BOUNDS. Logged for diagnosis.
            for (var wait = 0; wait < 50; wait++)
            {
                if (PresentationSource.FromVisual(bar.View) is HwndSource source &&
                    source.Handle != IntPtr.Zero)
                {
                    AppLog.Info($"bar view hwnd 0x{source.Handle:X} ready after {wait} polls, thread {Environment.CurrentManagedThreadId}");
                    break;
                }

                await Task.Delay(100);
            }

            // The control's HWND appears asynchronously after Loaded, so
            // attach with a few retries instead of failing on the first
            // 0x80070578 (invalid window handle).
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await bar.View.EnsureCoreWebView2Async(environment);
                    break;
                }
                catch (Exception error) when (attempt < 5)
                {
                    AppLog.Warn($"bar webview2 attach attempt {attempt} failed (0x{error.HResult:X8}): {error.Message}");
                    await Task.Delay(300);
                }
            }

            var settings = bar.View.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsZoomControlEnabled = false;
            bar.View.CoreWebView2.ProcessFailed += (_, args) =>
                AppLog.Warn($"bar browser process failed: {args.ProcessFailedKind}, exit={args.ExitCode}, reason={args.Reason}");
            bar.View.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    AppLog.Warn($"bar page navigation failed: {args.WebErrorStatus}");
                    return;
                }

                bar.Ready = true;
                bar.Send();
                AppLog.Info("bar page loaded");
            };
            bar.View.CoreWebView2.NavigateToString(BuildHtml());
        }
        catch (Exception error)
        {
            AppLog.Warn($"bar webview2 init failed: {error.Message}");
        }
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
            // A real size before Show: a 0x0 host window crashes the
            // WebView2 browser process (E_BOUNDS) when it attaches.
            window.Width = workArea.Width;
            window.Height = _config.Height;
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

    /// <summary>
    /// The strip's page: a flex row of workspace chips, tab titles and
    /// status cells. The configured font family and size are injected as
    /// a CSS font stack, so color emoji fall back to Segoe UI Emoji in
    /// Chromium. Status cells keep the widest width they have ever had,
    /// so one growing item never moves its neighbours (soft padding);
    /// padding slots are fixed spacers of that many characters.
    /// </summary>
    private string BuildHtml()
    {
        var family = _fontFamilyCss;
        var size = _fontSize.ToString(CultureInfo.InvariantCulture);
        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
            html, body { margin: 0; padding: 0; height: 100%; overflow: hidden; background: rgb(24 26 32); }
            body {
              font-family: {{family}}, sans-serif;
              font-size: {{size}}px;
              color: #fff;
              user-select: none;
              -webkit-user-select: none;
            }
            #bar { display: flex; align-items: center; height: 100%; box-sizing: border-box; }
            #chips, #tabs, #status { display: flex; align-items: center; }
            #tabs { flex: 1; overflow: hidden; }
            #status { margin-left: auto; }
            .chip {
              box-sizing: border-box; min-width: 28px; text-align: center;
              padding: 2px 8px; margin: 2px; font-weight: 600;
            }
            .tab {
              max-width: {{TabMaxWidth.ToString(CultureInfo.InvariantCulture)}}px;
              padding: 2px 10px; margin: 2px;
              white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
            }
            .active { background: #fff; color: #000; }
            .slot { white-space: nowrap; }
            </style>
            </head>
            <body>
            <div id="bar"><div id="chips"></div><div id="tabs"></div><div id="status"></div></div>
            <script>
            const chipsEl = document.getElementById('chips');
            const tabsEl = document.getElementById('tabs');
            const statusEl = document.getElementById('status');
            const slotWidths = [];
            let charWidth = 0;

            function cell(className, text) {
              const d = document.createElement('div');
              d.className = className;
              d.textContent = text;
              return d;
            }

            function measureChar() {
              const ctx = document.createElement('canvas').getContext('2d');
              ctx.font = '{{size}}px {{family}}';
              charWidth = ctx.measureText('0').width || {{size}} * 0.6;
            }

            function render(m) {
              chipsEl.replaceChildren(...m.chips.map(c => cell('chip' + (c.active ? ' active' : ''), c.label)));
              tabsEl.replaceChildren(...m.tabs.map(t => cell('tab' + (t.active ? ' active' : ''), t.title)));
              statusEl.replaceChildren(...m.status.map(s => cell('slot', s.text == null ? '' : s.text)));
              m.status.forEach((s, i) => {
                const d = statusEl.children[i];
                if (s.text == null) {
                  d.style.width = (Math.max(1, s.padding) * charWidth) + 'px';
                } else {
                  const w = d.offsetWidth;
                  if (w > (slotWidths[i] || 0)) { slotWidths[i] = w; }
                  d.style.width = slotWidths[i] + 'px';
                }
              });
            }

            measureChar();
            document.fonts.ready.then(measureChar);
            window.chrome.webview.addEventListener('message', e => render(e.data));
            </script>
            </body>
            </html>
            """;
    }
}
