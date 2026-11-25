namespace i3vm4windows.Config.Statements;

/// <summary>reload: restarts the whole application with the
/// configuration reloaded, like the tray menu's "Reload configuration"
/// — exec autostarts do not run again, exec_always do.</summary>
public sealed class ReloadStatement : CommandStatement
{
    public override string Name => "reload";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0)
        {
            context.Intent = new CommandIntent(
                Action: ConfigAction.Restart,
                Run: (runtime, _) => runtime.Restart());
        }
    }
}
