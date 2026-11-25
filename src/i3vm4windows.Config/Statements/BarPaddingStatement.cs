namespace i3vm4windows.Config.Statements;

/// <summary>
/// padding x: at the bar block's top level, a gap of x characters
/// between every status item; inside an item { ... } block, a spacer of
/// x characters between the item's parts. Padding is soft: items keep
/// the widest cell they have shown, so one item growing does not move
/// the others.
/// </summary>
public sealed class BarPaddingStatement : BarDirectiveStatement
{
    public override string Name => "padding";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count != 1 ||
            !int.TryParse(parameters[0], out var padding) ||
            padding < 0)
        {
            context.Warn($"padding needs a non-negative number: {context.Line}");
            return;
        }

        if (context.Bar.InItem)
        {
            context.Bar.AddPart(BarItemKind.Padding, padding: padding);
        }
        else
        {
            context.Bar.ItemPadding = padding;
        }
    }
}
