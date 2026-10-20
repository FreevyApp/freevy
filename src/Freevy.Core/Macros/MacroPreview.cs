namespace Freevy.Core.Macros;

/// <summary>One part of the editor preview: typed text, or a marker for a key press, wait or launch.</summary>
public sealed record PreviewPart(string Kind, string Value);

public sealed record MacroPreviewResult(bool HasMacros, IReadOnlyList<PreviewPart> Parts, int CursorBack, IReadOnlyList<string> Inputs);

/// <summary>What an expansion would type, for the editor: fill-ins shown as [name], the clipboard as [clipboard].</summary>
public static class MacroPreview
{
    public static MacroPreviewResult Describe(string text, Func<string, string?> findAbbreviation, DateTime? now = null)
    {
        var context = new MacroContext { FindAbbreviation = findAbbreviation, Clipboard = () => "[clipboard]", Now = now ?? DateTime.Now };
        var requests = MacroRenderer.InputsNeeded(text, context);
        var plan = MacroRenderer.Render(text, new MacroContext
        {
            FindAbbreviation = findAbbreviation,
            Clipboard = () => "[clipboard]",
            Now = context.Now,
            Inputs = requests.ToDictionary(r => r.Key, r => $"[{r.Label}]"),
        });
        var parts = plan.Steps.Select(step => step switch
        {
            TypeText t => new PreviewPart("text", t.Text),
            PressKeys k => new PreviewPart("key", (k.Action switch { KeyAction.Down => "hold ", KeyAction.Up => "release ", _ => "" }) + string.Join("+", k.Keys.Select(Title))),
            Wait w => new PreviewPart("wait", $"wait {w.Duration.TotalMilliseconds:0} ms"),
            LaunchTarget l => new PreviewPart("launch", $"open {l.Target}"),
            _ => new PreviewPart("text", ""),
        }).ToList();
        return new MacroPreviewResult(MacroParser.HasMacros(text), parts, plan.CursorBack, requests.Select(r => r.Label).ToList());
    }

    private static string Title(string key) => key.Length <= 1 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..];
}
