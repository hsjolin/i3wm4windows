using System.Text.RegularExpressions;
using i3vm4windows.Core;

namespace i3vm4windows.Config;

/// <summary>
/// Modifier flags parsed from i3 key names. The values match the Win32
/// MOD_* constants the hotkey host registers with.
/// </summary>
[Flags]
public enum Modifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>The tiling actions a binding can trigger.</summary>
public enum ConfigAction
{
    FocusPrevious,
    FocusNext,
    MovePrevious,
    MoveNext,
    SwitchWorkspace,
    MoveToWorkspace,
    ToggleOrientation,
    SetTabbed,
    SetSplit,
    SwitchBackAndForth,
    Mode,
    ResizeGrowWidth,
    ResizeShrinkWidth,
    ResizeGrowHeight,
    ResizeShrinkHeight,
    CloseFocused,
    Refresh,
    /// <summary>reload: restarts the whole app, reloading the
    /// configuration without re-running exec (like the tray menu).</summary>
    Restart,
    MoveWorkspaceToNextMonitor,
    Launch,
    FloatingToggle,
    ScratchpadShow,
}

/// <summary>
/// A bindsym entry: the key combination and the action it triggers. The key
/// is normalized (canonical names, upper-case letters, digits);
/// <paramref name="Command"/> carries the program for Launch bindings, the
/// mode name for Mode bindings;
/// <paramref name="Rule"/> targets windows for criteria commands like
/// scratchpad show; <paramref name="Mode"/> names the binding mode
/// the binding lives in ("default" outside mode blocks); and
/// <paramref name="Run"/> is the parsed command's runtime behavior,
/// executed against the runtime when the binding fires.
/// </summary>
public sealed record Binding(
    Modifiers Modifiers,
    string Key,
    ConfigAction Action,
    int Parameter,
    string? Command = null,
    WindowRule? Rule = null,
    string Mode = "default",
    Action<IConfigRuntime, WindowRule?>? Run = null);

/// <summary>The kind of one status item in the bar block.</summary>
public enum BarItemKind
{
    /// <summary>Literal text: print "...".</summary>
    Print,
    /// <summary>The first output line of a command: exec ...</summary>
    Command,
    /// <summary>CPU load as a bare number (0-100): cpu.</summary>
    Cpu,
    /// <summary>Memory load as a bare number (0-100): ram (memory).</summary>
    Ram,
    /// <summary>Battery charge as a bare number (0-100): battery.</summary>
    Battery,
    /// <summary>Fixed-width spacer: padding x.</summary>
    Padding,
}

/// <summary>One part of a status item: a print text, a command's
/// output, a value, or a spacer.</summary>
public sealed record BarPart(BarItemKind Kind, string Text = "", int Padding = 0);

/// <summary>One status item of the bar, in configuration order. A plain
/// directive (print, cpu, exec, padding, ...) makes a single-part item;
/// an item { ... } block makes a multi-part one, optionally with an
/// onclick command.</summary>
public sealed record BarItem
{
    /// <summary>A single-part item, as the plain bar directives make.</summary>
    public BarItem(BarItemKind Kind, string Text = "", int Padding = 0) =>
        Parts = new List<BarPart> { new(Kind, Text, Padding) };

    /// <summary>A multi-part item from an item { ... } block.</summary>
    public BarItem(IReadOnlyList<BarPart> Parts, string? OnClick = null)
    {
        this.Parts = Parts;
        this.OnClick = OnClick;
    }

    public IReadOnlyList<BarPart> Parts { get; }

    /// <summary>The command an onclick { ... } block attached, run when
    /// the item is clicked.</summary>
    public string? OnClick { get; }

    /// <summary>Single-part views of the item, for renderers and tests.</summary>
    public BarItemKind Kind => Parts.Count > 0 ? Parts[0].Kind : BarItemKind.Print;
    public string Text => Parts.Count > 0 ? Parts[0].Text : "";
    public int Padding => Parts.Count > 0 ? Parts[0].Padding : 0;
}

/// <summary>
/// The bar's configuration, from the bar block of the configuration
/// file. The defaults describe the plain bar: top of every display,
/// 30 pixels tall, no status output.
/// </summary>
/// <param name="ItemPadding">Blank padding of this many characters
/// between the status items (the bar block's top-level padding,
/// default 1): half on each side of the separator.</param>
public sealed record BarConfig(
    string Position = "top",
    int Height = 30,
    bool Clock = false,
    bool Date = false,
    IReadOnlyList<string>? StatusCommands = null,
    IReadOnlyList<BarItem>? Items = null,
    string? Font = null,
    double FontSize = 13,
    string Separator = "",
    int ItemPadding = 1)
{
    public IReadOnlyList<string> Commands => StatusCommands ?? Array.Empty<string>();

    /// <summary>The status items in configuration order (print, exec,
    /// cpu, ram, battery, padding).</summary>
    public IReadOnlyList<BarItem> BarItems => Items ?? Array.Empty<BarItem>();

    /// <summary>The bar font family; Segoe UI Emoji as fallback renders
    /// color emoji. Overridden by the bar block's font directive.</summary>
    public string BarFont => Font ?? "Segoe UI, Segoe UI Emoji";
}

/// <summary>The result of parsing a configuration file.</summary>
public sealed record I3Config(
    IReadOnlyList<Binding> Bindings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<WindowRule> Rules,
    IReadOnlyList<string> Exec,
    int MinWindowWidth = 400,
    LayoutMode DefaultWorkspaceLayout = LayoutMode.Split,
    bool FocusFollowsMouse = false,
    BarConfig? Bar = null,
    IReadOnlyList<string>? ExecAlways = null)
{
    /// <summary>The exec_always commands: they run on a real start and on
    /// every reload; plain exec only on a real start.</summary>
    public IReadOnlyList<string> AutostartsAlways => ExecAlways ?? Array.Empty<string>();
}

/// <summary>What a for_window rule does with the windows it matches.</summary>
public enum WindowRuleAction
{
    /// <summary>Matching windows are never managed ("floating enable").</summary>
    Floating,

    /// <summary>Matching windows join the given workspace when they appear.</summary>
    MoveToWorkspace,

    /// <summary>Matching windows appear directly into the scratchpad,
    /// hidden until a scratchpad show binding reveals them.</summary>
    Scratchpad,
}

/// <summary>
/// A for_window rule: windows whose class and/or title match the regular
/// expressions get the rule's action applied when they appear — "floating
/// enable" leaves them entirely alone (this app has no floating layout, so
/// they are simply never managed), "move container to workspace" makes
/// them join that workspace instead of the active one.
/// </summary>
public sealed record WindowRule(
    Regex? Class,
    Regex? Title,
    WindowRuleAction Action = WindowRuleAction.Floating,
    int Parameter = 0)
{
    public bool Matches(string windowClass, string title) =>
        (Class is null || Class.IsMatch(windowClass)) &&
        (Title is null || Title.IsMatch(title));
}
