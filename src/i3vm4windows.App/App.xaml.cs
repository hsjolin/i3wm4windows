using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using i3vm4windows.App.Interop;
using i3vm4windows.App.Services;
using i3vm4windows.Config;
using i3vm4windows.Config.Extensions;
using i3vm4windows.Core;
using Binding = i3vm4windows.Config.Binding;

namespace i3vm4windows.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private readonly Dictionary<int, Binding> _bindings = new();
    /// <summary>The bindings of each mode except the default one, by mode
    /// name; the hook activates them while the mode is active.</summary>
    private readonly Dictionary<string, List<(int Id, Binding Binding)>> _modes = new();
    private string _configPath = "";
    private string _startupArgs = "";
    private WindowManager? _windowManager;
    private HotkeyHost? _hotkeyHost;
    private WinDragger? _dragger;
    private ConfigRuntime? _runtime;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Dev helper: regenerate app.ico from the same drawing the tray
        // icon uses, so both are always identical.
        var writeIcon = Array.IndexOf(e.Args, "--write-icon");
        if (writeIcon >= 0 && writeIcon + 1 < e.Args.Length)
        {
            AppIcon.WriteToFile(e.Args[writeIcon + 1]);
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);
        AppLog.Session();

        // One instance only: a window manager running twice would fight
        // over the same hooks, windows and tray icon. A reload restarts
        // the app, and the new process starts before the old one has
        // exited, so a second start waits for the mutex — released in
        // OnExit — instead of giving up at once.
        _singleInstance = new Mutex(false, @"Global\i3vm4windows.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            AppLog.Info("another instance is running; waiting for it to exit");
            try
            {
                createdNew = _singleInstance.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                // The previous instance died without releasing; the
                // mutex is ours now.
                createdNew = true;
            }
        }

        if (!createdNew)
        {
            AppLog.Warn("another instance is still running; exiting");
            Environment.Exit(0);
            return;
        }

        _configPath = e.Args.FirstOrDefault(a => !a.StartsWith('-'))
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "i3", "config");
        _startupArgs = string.Join(" ", e.Args.Where(a => !a.StartsWith('-')));
        AppLog.Info($"configuration: {_configPath}");

        var config = LoadConfig(_configPath);
        foreach (var warning in config.Warnings)
        {
            AppLog.Warn($"configuration: {warning}");
        }

        AppLog.Info($"configuration: {config.Bindings.Count} bindings, " +
            $"{config.Rules.Count} rules, {config.Exec.Count} autostarts");

        _windowManager = new WindowManager(config.Rules, LayoutOptionsFor(config), config.Bar)
        {
            FocusFollowsMouse = config.FocusFollowsMouse,
        };
        _windowManager.Start();

        _hotkeyHost = new HotkeyHost();
        _hotkeyHost.Start(HandleHotkey);
        RegisterHotkeys(config);

        // The statements carry their own runtime behavior; this is what
        // they execute it against.
        _runtime = new ConfigRuntime(_windowManager, name => SetMode(name), RestartApplication);

        // Win + mouse drag moves and resizes windows; only this app can
        // see the Win key, so it owns the whole gesture.
        _dragger = new WinDragger(
            () => _hotkeyHost.WinKeyDown,
            hwnd => _windowManager?.ReleaseForDragging(hwnd),
            Dispatcher);
        _dragger.Start();

        // i3's autostart: on a real start, exec and exec_always run; a
        // reload restarts with --ignore-exec, so only exec_always runs.
        var ignoreExec = e.Args.Contains("--ignore-exec", StringComparer.OrdinalIgnoreCase);
        AppLog.Info($"startup: exec {(ignoreExec ? "skipped (--ignore-exec)" : "runs")}, " +
            $"exec_always always runs");
        if (!ignoreExec)
        {
            foreach (var command in config.Exec)
            {
                Launcher.Start(command);
            }
        }

        foreach (var command in config.AutostartsAlways)
        {
            Launcher.Start(command);
        }

        _trayIcon = CreateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _dragger?.Dispose();
        _hotkeyHost?.Dispose();
        _windowManager?.Dispose();
        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Already released.
        }

        base.OnExit(e);

        // The WebView2 browser process outlives Application.Shutdown, so
        // without this a reloaded instance would linger with its tray icon.
        Environment.Exit(0);
    }

    /// <summary>
    /// The configuration to use: the file named on the command line, else
    /// the i3-style default location in the user profile. When the file is
    /// missing or yields no usable bindings, the built-in defaults apply.
    /// </summary>
    private static I3Config LoadConfig(string path) =>
        TryReadConfig(path) ?? I3ConfigParser.Parse(I3Defaults.Text);

    /// <summary>
    /// Reads and parses the configuration file, when it yields usable
    /// bindings; null means the caller should keep what it has.
    /// </summary>
    private static I3Config? TryReadConfig(string path)
    {
        try
        {
            var config = I3ConfigParser.Parse(File.ReadAllText(path));
            if (config.Bindings.Count > 0)
            {
                return config;
            }

            AppLog.Warn($"configuration: {path} has no usable bindings");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"configuration: cannot read {path}: {error.Message}");
        }

        return null;
    }

    /// <summary>
    /// Restarts the app completely, like exiting and starting it again:
    /// the running instance shuts down — restoring hidden windows and
    /// releasing its hooks — while a fresh instance starts and loads the
    /// configuration from scratch.
    /// </summary>
    private void RestartApplication()
    {
        AppLog.Info("restarting on request (config reload, --ignore-exec)");

        if (Environment.ProcessPath is { } exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"--ignore-exec {_startupArgs}".Trim(),
                    UseShellExecute = true,
                });
            }
            catch (Exception error)
            {
                AppLog.Warn($"cannot restart {exe}: {error.Message}");
            }
        }

        Shutdown();
    }

    /// <summary>
    /// The layout tuning the configuration controls: gaps are fixed for
    /// now, the minimum window width comes from the min_window_width
    /// directive (400 when the configuration does not set one) and the
    /// layout of new workspaces from workspace_layout.
    /// </summary>
    private static LayoutOptions LayoutOptionsFor(I3Config config) => new()
    {
        OuterMargin = 8,
        InnerGap = 8,
        MinWindowWidth = config.MinWindowWidth,
        DefaultLayoutMode = config.DefaultWorkspaceLayout,
    };

    private void RegisterHotkeys(I3Config config)
    {
        if (_hotkeyHost is null)
        {
            return;
        }

        var nextId = 1;
        foreach (var binding in config.Bindings)
        {
            if (!binding.Key.TryConvertToEnum<Key>(out var key))
            {
                continue;
            }

            var id = nextId++;
            if (binding.Mode == "default")
            {
                // Config.Modifiers uses the Win32 MOD_* values.
                _hotkeyHost.Register(id, (Win32.Modifiers)binding.Modifiers, key);
            }
            else
            {
                // Mode bindings dispatch through the hook's mode matching
                // only while their mode is active.
                if (!_modes.TryGetValue(binding.Mode, out var list))
                {
                    list = new List<(int, Binding)>();
                    _modes[binding.Mode] = list;
                }

                list.Add((id, binding));
            }

            _bindings[id] = binding;
        }
    }

    /// <summary>
    /// Enters the named binding mode, like i3's mode command: its bindings
    /// replace the default ones until another mode command leaves it.
    /// </summary>
    private void SetMode(string? name)
    {
        var mode = string.IsNullOrEmpty(name) ||
            name.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? "default"
                : name;
        var bindings = mode != "default" && _modes.TryGetValue(mode, out var list)
            ? list
            : new List<(int Id, Binding Binding)>();

        var registered = new List<(int Id, Win32.Modifiers Modifiers, Key Key)>();
        foreach (var (id, binding) in bindings)
        {
            if (binding.Key.TryConvertToEnum<Key>(out var key))
            {
                registered.Add((id, (Win32.Modifiers)binding.Modifiers, key));
            }
        }

        _hotkeyHost?.SetModeBindings(registered);
    }



    private void HandleHotkey(int id)
    {
        if (!_bindings.TryGetValue(id, out var binding))
        {
            return;
        }

        AppLog.Info($"hotkey: {binding.Action} ({binding.Key}, {binding.Mode} mode)");

        // The command's own statement parsed its behavior; running it is
        // all the app does here.
        binding.Run?.Invoke(_runtime!, binding.Rule);
    }

    /// <summary>When the log was last opened, to swallow a double-fired
    /// menu click.</summary>
    private static DateTime _logOpenedAt = DateTime.MinValue;

    /// <summary>
    /// Opens the log in Notepad — reusing a Notepad window that already
    /// shows it, so repeated clicks do not pile up windows.
    /// </summary>
    private static void OpenLog()
    {
        AppLog.Info("tray: open log");
        if ((DateTime.Now - _logOpenedAt).TotalSeconds < 2)
        {
            AppLog.Info("tray: open log again within 2 s; ignoring");
            return;
        }

        _logOpenedAt = DateTime.Now;
        foreach (var process in Process.GetProcessesByName("notepad"))
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero &&
                    process.MainWindowTitle.Contains("log.txt", StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info("tray: log already open in notepad; activating it");
                    Win32.SetForegroundWindow(process.MainWindowHandle);
                    return;
                }
            }
            catch
            {
                // A Notepad window that is closing or has none yet.
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = $"\"{AppLog.Path}\"",
            UseShellExecute = true,
        });
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Reload configuration (no exec)", null, (_, _) => RestartApplication());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add("Exit i3vm4windows", null, (_, _) => Shutdown());

        var icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "i3vm4windows",
            Icon = AppIcon.Create(),
            Visible = true,
            ContextMenuStrip = menu,
        };

        return icon;
    }
}
