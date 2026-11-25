namespace i3vm4windows.Config.Statements;

/// <summary>
/// separator text: drawn between the bar's status items, like i3status'
/// separator. Without text the separator is off.
/// </summary>
public sealed class BarSeparatorStatement : BarDirectiveStatement
{
    public override string Name => "separator";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        context.Bar.Separator = string.Join(' ', parameters);
    }
}
