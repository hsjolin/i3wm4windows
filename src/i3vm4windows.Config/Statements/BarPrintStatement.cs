namespace i3vm4windows.Config.Statements;

/// <summary>
/// print "text": a literal text item of the bar's status line, e.g.
/// print "% CPU" next to a bare cpu number.
/// </summary>
public sealed class BarPrintStatement : BarDirectiveStatement
{
    public override string Name => "print";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0)
        {
            context.Warn($"print without text: {context.Line}");
            return;
        }

        context.Bar.AddPart(BarItemKind.Print, string.Join(' ', parameters));
    }
}
