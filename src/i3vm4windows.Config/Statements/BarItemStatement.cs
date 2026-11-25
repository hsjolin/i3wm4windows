namespace i3vm4windows.Config.Statements;

/// <summary>
/// item { ... }: opens a composite status item of the bar block; the
/// directives inside it (print, exec, cpu, ram, battery, padding) become
/// parts of one item, and onclick { ... } attaches a click command.
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

        if (parameters.Count == 1 && parameters[0] == "{" && !context.Bar.InItem)
        {
            context.Bar.InItem = true;
            context.Bar.ItemParts.Clear();
            context.Bar.ItemOnClick = null;
            return;
        }

        context.Warn($"Unsupported item directive: {context.Line}");
    }
}
