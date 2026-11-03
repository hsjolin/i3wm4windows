namespace i3vm4windows.Config.Statements;

/// <summary>
/// focus_follows_mouse yes|no: whether moving the pointer onto a managed
/// window of the active workspace focuses it. Parse-time data like
/// workspace_layout: off unless the configuration asks for it.
/// </summary>
public sealed class FocusFollowsMouseStatement : TopLevelStatement
{
    public override string Name => "focus_follows_mouse";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1)
        {
            switch (parameters[0].ToLowerInvariant())
            {
                case "yes" or "true" or "1":
                    context.FocusFollowsMouse = true;
                    return;
                case "no" or "false" or "0":
                    context.FocusFollowsMouse = false;
                    return;
            }
        }

        context.Warn($"Unsupported focus_follows_mouse: {context.Line}");
    }
}
