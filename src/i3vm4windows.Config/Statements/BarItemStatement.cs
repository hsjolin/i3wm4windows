namespace i3vm4windows.Config.Statements;

/// <summary>
/// item { ... }: opens a composite status item of the bar block; the
/// directives inside it (print, exec, cpu, ram, battery, padding) become
/// parts of one item, onclick { ... } attaches a click command, and
/// nested item { ... } blocks become the item's children. Inside an item,
/// separator and padding set the gap between the children.
/// </summary>
public sealed class BarItemStatement : IConfigStatement
{
    public string Name => "item";

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (!context.InBar)
        {
            context.Warn($"item does not belong outside the bar block: {context.Line}");
            return;
        }

        if (parameters.Count == 1 && parameters[0] == "{" && !context.Bar.InOnClick)
        {
            context.Bar.OpenItem();
            return;
        }

        context.Warn($"Unsupported item directive: {context.Line}");
    }
}
