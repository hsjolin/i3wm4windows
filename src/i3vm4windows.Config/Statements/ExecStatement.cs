namespace i3vm4windows.Config.Statements;

/// <summary>exec "program": starts the program from a binding, runs it
/// from the bar block as a status command, and at the top level is i3's
/// autostart.</summary>
public class ExecStatement : CommandStatement
{
    public override string Name => "exec";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        var rest = StripMarker(parameters);
        if (rest.Length == 0)
        {
            // A binding's bare exec warns as the unsupported command it
            // is; the top level and the bar keep their own wording.
            if (!context.CollectingIntent)
            {
                context.Warn($"exec without a program: {context.Line}");
            }

            return;
        }

        if (context.InBar)
        {
            var commandText = string.Join(' ', rest);
            if (context.Bar.InOnClick)
            {
                // The onclick block's command: run when the item is clicked.
                context.Bar.ItemOnClick = commandText;
                return;
            }

            if (context.Bar.InItem)
            {
                context.Bar.AddPart(BarItemKind.Command, commandText);
                return;
            }

            context.Bar.Commands.Add(commandText);
            context.Bar.Items.Add(new BarItem(BarItemKind.Command, commandText));
            return;
        }

        if (context.CollectingIntent)
        {
            // The program name and arguments keep their original case;
            // only the --no-startup-id marker of i3 is dropped.
            var commandText = string.Join(' ', rest);
            context.Intent = new CommandIntent(
                Action: ConfigAction.Launch,
                Command: commandText,
                Run: (runtime, _) => runtime.Launch(commandText));
            return;
        }

        AddAutostart(context, string.Join(' ', rest));
    }

    /// <summary>Where a top-level command lands: exec autostarts only on a
    /// real start, exec_always also on a reload.</summary>
    protected virtual void AddAutostart(ConfigContext context, string command) =>
        context.Autostarts.Add(command);

    /// <summary>Drops the --no-startup-id marker of i3.</summary>
    private static string[] StripMarker(IReadOnlyList<string> parameters)
    {
        var rest = ConfigGrammar.Rest(parameters, 0);
        if (rest.Length > 0 &&
            rest[0].Equals("--no-startup-id", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[1..];
        }

        return rest;
    }
}
