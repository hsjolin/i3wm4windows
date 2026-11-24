using System.Globalization;

namespace i3vm4windows.Config.Statements;

/// <summary>
/// font "family" [size]: the bar's font, i3 syntax (an optional
/// pango: prefix is ignored). The family may list fallbacks,
/// e.g. font "Segoe UI, Segoe UI Emoji" 13 for color emoji.
/// </summary>
public sealed class BarFontStatement : BarDirectiveStatement
{
    public override string Name => "font";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count is < 1 or > 2)
        {
            context.Warn($"font needs a family and optional size: {context.Line}");
            return;
        }

        var family = parameters[0].Trim('"');
        if (family.StartsWith("pango:", StringComparison.OrdinalIgnoreCase))
        {
            family = family["pango:".Length..];
        }

        if (parameters.Count == 2 &&
            (!double.TryParse(parameters[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ||
             size is <= 0 or > 100))
        {
            context.Warn($"font size must be a number between 0 and 100: {context.Line}");
            return;
        }

        context.Bar.Font = family;
        if (parameters.Count == 2)
        {
            context.Bar.FontSize = double.Parse(parameters[1], CultureInfo.InvariantCulture);
        }
    }
}
