namespace i3vm4windows.Config.Statements;

/// <summary>
/// A bare value item of the bar block: cpu, ram (memory) or battery.
/// The item renders only the number (0-100); labels and units belong to
/// print items around it. The old yes/no flag form is gone — the
/// directive always adds the item.
/// </summary>
public sealed class BarValueStatement : BarDirectiveStatement
{
    private readonly BarItemKind _kind;

    public BarValueStatement(string name, BarItemKind kind)
    {
        Name = name;
        _kind = kind;
    }

    public override string Name { get; }

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count > 0)
        {
            context.Warn($"{Name} takes no yes/no flag anymore: {context.Line}");
            return;
        }

        context.Bar.AddPart(_kind);
    }
}
