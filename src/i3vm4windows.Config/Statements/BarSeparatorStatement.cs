namespace i3vm4windows.Config.Statements;

/// <summary>
/// separator text: drawn between the bar's status items, like i3status'
/// separator. Inside an item { ... } block it sets the separator between
/// that item's children instead (children get none unless set there).
/// Without text the separator is off.
/// </summary>
public sealed class BarSeparatorStatement : BarDirectiveStatement
{
    public override string Name => "separator";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        var text = string.Join(' ', parameters);
        if (context.Bar.CurrentItem is { } item)
        {
            item.Separator = text;
        }
        else
        {
            context.Bar.Separator = text;
        }
    }
}
