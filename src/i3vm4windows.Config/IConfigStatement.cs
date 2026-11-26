using i3vm4windows.Core;

namespace i3vm4windows.Config;

/// <summary>
/// One statement of the configuration file, resolved from the IoC
/// container by its name: bindsym, exec, set — and the commands a
/// bindsym nests (exec "program", workspace 3, layout tabbed, ...).
/// Execute applies the statement with its parameters to the
/// configuration being parsed.
/// </summary>
public interface IConfigStatement
{
    /// <summary>The statement's name as it appears in the configuration.</summary>
    string Name { get; }

    /// <summary>
    /// Executes the statement with its parameters. A statement whose
    /// parameters are statements of their own (bindsym's command)
    /// resolves them through the context and collects their intent.
    /// </summary>
    void Execute(ConfigContext context, IReadOnlyList<string> parameters);
}

/// <summary>
/// The outcome of a command statement, collected by the statement that
/// resolves it: the action a binding triggers, or the rule a for_window
/// directive builds — and the command's runtime behavior, which the
/// statement that parsed it owns: the Run delegate executes against the
/// runtime, with the binding's criteria-carrying rule as its second
/// argument.
/// </summary>
public sealed record CommandIntent(
    ConfigAction? Action = null,
    WindowRuleAction? Rule = null,
    int Parameter = 0,
    string? Command = null,
    Action<IConfigRuntime, WindowRule?>? Run = null);

/// <summary>
/// Everything one parse accumulates: the bindings, rules, autostarts
/// and bar settings being built, the variables, the mode and bar
/// blocks' state, and the warnings. Statements resolve their own
/// parameters as statements through the resolver.
/// </summary>
public sealed class ConfigContext
{
    private readonly Dictionary<string, IConfigStatement> _statements =
        new(StringComparer.OrdinalIgnoreCase);

    public ConfigContext(IEnumerable<IConfigStatement> statements)
    {
        foreach (var statement in statements)
        {
            _statements[statement.Name] = statement;
        }
    }

    /// <summary>The statement with the given name, or null when the
    /// configuration carries a statement this app has no implementation
    /// for.</summary>
    public IConfigStatement? Resolve(string name) =>
        _statements.GetValueOrDefault(name);

    /// <summary>Later bindsym lines win over earlier ones for the same
    /// key combination, like i3 replaces bindings; the mode is part of
    /// the key so default and mode bindings coexist.</summary>
    public Dictionary<(string Mode, Modifiers Modifiers, string Key), Binding> Bindings { get; } = new();

    public List<WindowRule> Rules { get; } = new();

    public Dictionary<string, string> Variables { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> Autostarts { get; } = new();

    /// <summary>The exec_always commands: like autostarts, but they also
    /// run when the configuration is reloaded without a restart.</summary>
    public List<string> AutostartsAlways { get; } = new();

    /// <summary>The smallest width a window keeps before the layout
    /// overlaps windows instead of shrinking them further.</summary>
    public int MinWindowWidth { get; set; } = 400;

    /// <summary>The layout new workspaces start in (i3's
    /// workspace_layout).</summary>
    public LayoutMode WorkspaceLayout { get; set; } = LayoutMode.Split;

    /// <summary>Whether hovering a window focuses it (i3's
    /// focus_follows_mouse); off unless the configuration asks for it.</summary>
    public bool FocusFollowsMouse { get; set; }

    /// <summary>The binding mode whose block is open, or null in the
    /// default mode.</summary>
    public string? Mode { get; set; }

    /// <summary>Whether the bar block is open.</summary>
    public bool InBar { get; set; }

    /// <summary>The bar settings as they are parsed.</summary>
    public BarSettings Bar { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>The line being parsed, for warnings.</summary>
    public string Line { get; set; } = "";

    /// <summary>
    /// The intent of the command statement being collected by the
    /// statement that resolved it (bindsym, for_window); command
    /// statements write here, the collector reads and clears.
    /// </summary>
    public CommandIntent? Intent { get; set; }

    /// <summary>Whether a binding or rule builder is collecting the
    /// command being executed, so statements that mean different things
    /// at the top level (exec) act as commands.</summary>
    public bool CollectingIntent { get; set; }

    public void Warn(string message) => Warnings.Add(message);
}

/// <summary>The bar block's directives as they are parsed.</summary>
public sealed class BarSettings
{
    public string Position = "top";
    public int Height = 30;
    public bool Clock;
    public bool Date;
    public List<string> Commands = new();

    /// <summary>The status items in the order the bar block lists
    /// them: print, padding, cpu, ram, battery, exec.</summary>
    public List<BarItem> Items = new();

    /// <summary>The bar block's font directive: family and optional
    /// size in px (i3 syntax: font pango:family 13).</summary>
    public string? Font;
    public double? FontSize;

    /// <summary>The separator text drawn between status items
    /// (separator |); empty draws none.</summary>
    public string Separator = "";

    /// <summary>The top-level padding directive: blank space of this
    /// many characters between the status items, half on each side of
    /// the separator. Null until set; the bar defaults it to 1.</summary>
    public int? ItemPadding;

    /// <summary>The open item { ... } blocks, outermost first; item
    /// blocks nest, a child block closes into its parent's children.</summary>
    private readonly Stack<ItemBuilder> _itemStack = new();

    /// <summary>Whether an item { ... } block is open.</summary>
    public bool InItem => _itemStack.Count > 0;

    /// <summary>Whether an onclick { ... } block is open inside the
    /// innermost item.</summary>
    public bool InOnClick;

    /// <summary>The innermost open item block, or null.</summary>
    public ItemBuilder? CurrentItem => _itemStack.Count > 0 ? _itemStack.Peek() : null;

    /// <summary>Opens an item { ... } block, nested when one is open.</summary>
    public void OpenItem() => _itemStack.Push(new ItemBuilder());

    /// <summary>Adds one part where it belongs: to the innermost open
    /// item block, or as a standalone item when none is open.</summary>
    public void AddPart(BarItemKind kind, string text = "", int padding = 0)
    {
        if (CurrentItem is { } item)
        {
            item.Parts.Add(new BarPart(kind, text, padding));
        }
        else
        {
            Items.Add(new BarItem(kind, text, padding));
        }
    }

    /// <summary>Closes the innermost item block: the collected parts,
    /// onclick command, separator/padding overrides and child items
    /// become one item — a child of the enclosing item, if any.</summary>
    public void CloseItem()
    {
        if (_itemStack.Count == 0)
        {
            return;
        }

        var builder = _itemStack.Pop();
        var item = new BarItem(
            builder.Parts, builder.OnClick, builder.Children, builder.Separator, builder.Padding);
        if (_itemStack.Count > 0)
        {
            _itemStack.Peek().Children.Add(item);
        }
        else if (builder.Parts.Count > 0 || builder.OnClick is not null || builder.Children.Count > 0)
        {
            Items.Add(item);
        }
    }

    public BarConfig ToConfig() => new(
        Position, Height, Clock, Date, Commands, Items, Font, FontSize ?? 13, Separator, ItemPadding ?? 1);
}

/// <summary>One open item { ... } block while parsing: the parts,
/// onclick command, separator/padding overrides and nested child items
/// collected for it so far.</summary>
public sealed class ItemBuilder
{
    public List<BarPart> Parts = new();
    public string? OnClick;
    public List<BarItem> Children = new();
    public string? Separator;
    public int? Padding;
}
