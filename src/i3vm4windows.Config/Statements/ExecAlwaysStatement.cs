namespace i3vm4windows.Config.Statements;

/// <summary>
/// exec_always "program": like exec, but i3 runs it again on every reload
/// as well. This app reloads by restarting completely, so here it behaves
/// exactly like exec: an autostart at the top level, a launch command in
/// a binding.
/// </summary>
public sealed class ExecAlwaysStatement : ExecStatement
{
    public override string Name => "exec_always";
}
