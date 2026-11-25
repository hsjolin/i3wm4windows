namespace i3vm4windows.Config.Statements;

/// <summary>
/// onclick { ... }: inside an item block, the command to run when the
/// item is clicked (onclick { exec taskmgr }).
/// </summary>
public sealed class BarOnClickStatement : IConfigStatement
{
    public string Name => "onclick";

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (context.InBar && context.Bar.InItem &&
            parameters.Count == 1 && parameters[0] == "{" && !context.Bar.InOnClick)
        {
            context.Bar.InOnClick = true;
            return;
        }

        context.Warn($"onclick belongs in an item block of the bar: {context.Line}");
    }
}
