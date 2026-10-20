using System.Globalization;
using Freevy.Core.Macros;

namespace Freevy.Core.Tests;

public class MacroTests
{
    private static readonly DateTime Now = new(2026, 3, 5, 14, 7, 9); // Thursday
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static MacroContext Context(Func<string, string?>? abbreviations = null, Dictionary<string, string>? inputs = null, string? clipboard = null) => new()
    {
        Now = Now,
        Culture = English,
        Clipboard = () => clipboard,
        FindAbbreviation = abbreviations ?? (_ => null),
        Inputs = inputs ?? [],
    };

    private static OutputPlan Render(string text, MacroContext? context = null) => MacroRenderer.Render(text, context ?? Context());

    [Fact]
    public void Plain_text_and_unknown_macros_stay_as_typed()
    {
        Assert.Equal("100% (sure) %(Nope) %(Key Banana)", Render("100% (sure) %(Nope) %(Key Banana)").TypedText);
        Assert.Equal("literal %(Day)", Render("literal %%(Day)").TypedText);
        Assert.False(MacroParser.HasMacros("50% off (today)"));
    }

    [Fact]
    public void Shift_enter_is_a_key_press_between_the_lines()
    {
        var plan = Render("Regards,%(Key Shift+Enter)Ozan Kurt");

        Assert.Equal(3, plan.Steps.Count);
        Assert.Equal(new TypeText("Regards,"), plan.Steps[0]);
        var keys = Assert.IsType<PressKeys>(plan.Steps[1]);
        Assert.Equal(["shift", "enter"], keys.Keys);
        Assert.Equal(KeyAction.Press, keys.Action);
        Assert.Equal(new TypeText("Ozan Kurt"), plan.Steps[2]);
    }

    [Theory]
    [InlineData("Regards,%(Key Shift+Enter)\nOzan Kurt")]
    [InlineData("Regards,%(Key Shift+Enter)\r\nOzan Kurt")]
    [InlineData("Regards,%(key shift+enter)\nOzan Kurt")]
    public void A_line_break_after_an_enter_macro_is_only_layout(string text)
    {
        // PressKeys holds a list, so records compare by reference; compare a flat description.
        static string Describe(OutputPlan plan) =>
            string.Join("|", plan.Steps.Select(s => s is PressKeys k ? "keys:" + string.Join("+", k.Keys) : s.ToString()));

        Assert.Equal(Describe(Render("Regards,%(Key Shift+Enter)Ozan Kurt")), Describe(Render(text)));
    }

    [Fact]
    public void Only_the_first_line_break_after_an_enter_macro_is_dropped()
    {
        var plan = Render("a%(Key Enter)\n\nb");

        Assert.Equal(new TypeText("\nb"), plan.Steps[^1]);
    }

    [Fact]
    public void A_line_break_after_other_keys_is_kept()
    {
        var plan = Render("a%(Key Tab)\nb");

        Assert.Equal(new TypeText("\nb"), plan.Steps[^1]);
    }

    [Fact]
    public void Macro_names_ignore_case_so_case_adaptation_cannot_break_them()
    {
        var plan = Render("A%(KEY SHIFT+ENTER)B %(DAY)");
        Assert.IsType<PressKeys>(plan.Steps[1]);
        Assert.Equal("AB 05", plan.TypedText);
    }

    [Theory]
    [InlineData("%(Day)/%(Month)/%(Year)", "05/03/2026")]
    [InlineData("%(#Day).%(#Month).%(YearShort)", "5.3.26")]
    [InlineData("%(WeekdayName), %(MonthName) %(Day)", "Thursday, March 05")]
    [InlineData("%(WeekdayNameShort) %(MonthNameShort)", "Thu Mar")]
    [InlineData("%(Hour):%(Minute):%(Second) %(AMorPM)", "02:07:09 PM")]
    [InlineData("%(#Hour) %(Hour24) %(#Hour24) %(#Minute) %(#Second)", "2 14 14 7 9")]
    [InlineData("%(#DayOfYear) %(#DayOfWeek) %(#DayOfWeekMon) %(#WeekOfYear)", "64 5 4 10")]
    public void Date_and_time_macros(string text, string expected)
    {
        Assert.Equal(expected, Render(text).TypedText);
    }

    [Fact]
    public void Date_math_shifts_the_dates_that_follow()
    {
        Assert.Equal("05 then 12 then 05/02", Render("%(Day) then %(AddDays 7)%(Day) then %(SubtractDays 7)%(SubMonths 1)%(Day)/%(Month)").TypedText);
    }

    [Fact]
    public void Cursor_moves_back_over_what_follows_it()
    {
        var plan = Render("<b>%(cursor)</b>");
        Assert.Equal("<b></b>", plan.TypedText);
        Assert.Equal(4, plan.CursorBack);
        Assert.Equal(0, Render("no cursor").CursorBack);
    }

    [Fact]
    public void Clipboard_and_fill_ins()
    {
        var context = Context(inputs: new() { ["name"] = "Ada", ["#1"] = "first", ["#2"] = "second" }, clipboard: "pasted\r\ntext");

        Assert.Equal("Hi Ada, again Ada: first second pasted\ntext", MacroRenderer.Render("Hi %(Input name), again %(Fill name): %(Input) %(Input) %(clipboard)", context).TypedText);

        var requests = MacroRenderer.InputsNeeded("%(Input name) %(Fill Name) %(Input) %(Input)", context);
        Assert.Equal(["name", "#1", "#2"], requests.Select(r => r.Key));
        Assert.Equal(["name", "Text", "Text 2"], requests.Select(r => r.Label));
    }

    [Fact]
    public void Nested_abbreviations_expand_and_stop_at_cycles()
    {
        var snippets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["myname"] = "Ozan",
            ["greet"] = "Hello, I am %(Abbrev myname)",
            ["loop"] = "x%(Abbrev loop)",
        };
        var context = Context(snippets.GetValueOrDefault);

        Assert.Equal("Hello, I am Ozan!", MacroRenderer.Render("%(Abbreviation greet)!", context).TypedText);
        Assert.Equal("x%(Abbrev loop)", MacroRenderer.Render("%(Abbrev loop)", context).TypedText);
        Assert.Equal("%(Abbrev missing)", MacroRenderer.Render("%(Abbrev missing)", context).TypedText);
    }

    [Fact]
    public void Keys_delays_launches_and_formatting_shortcuts()
    {
        var plan = Render("%(B)bold%(B)%(Delay 1s100ms)%(KeyDown Ctrl)%(KeyUp Ctrl)%(Launch \"C:\\Program Files\\App\\app.exe\" --new)%(Launch https://freevy.app)");

        Assert.Equal(new PressKeys(["ctrl", "b"], KeyAction.Press), plan.Steps[0], new PressKeysComparer());
        Assert.Equal(new Wait(TimeSpan.FromMilliseconds(1100)), plan.Steps[3]);
        Assert.Equal(KeyAction.Down, ((PressKeys)plan.Steps[4]).Action);
        Assert.Equal(KeyAction.Up, ((PressKeys)plan.Steps[5]).Action);
        Assert.Equal(new LaunchTarget(@"C:\Program Files\App\app.exe", "--new"), plan.Steps[6]);
        Assert.Equal(new LaunchTarget("https://freevy.app", ""), plan.Steps[7]);
    }

    [Fact]
    public void Absurd_delays_are_not_macros()
    {
        Assert.Equal("%(Delay 99m)", Render("%(Delay 99m)").TypedText);
    }

    [Fact]
    public void Preview_shows_keys_fill_ins_and_the_clipboard_as_markers()
    {
        var preview = MacroPreview.Describe("Hi %(Input name),%(Key Shift+Enter)%(clipboard)%(cursor)!", _ => null, Now);

        Assert.True(preview.HasMacros);
        Assert.Equal(["text", "key", "text"], preview.Parts.Select(p => p.Kind));
        Assert.Equal("Hi [name],", preview.Parts[0].Value);
        Assert.Equal("Shift+Enter", preview.Parts[1].Value);
        Assert.Equal("[clipboard]!", preview.Parts[2].Value);
        Assert.Equal(1, preview.CursorBack);
        Assert.Equal(["name"], preview.Inputs);
        Assert.False(MacroPreview.Describe("plain", _ => null).HasMacros);
    }

    private sealed class PressKeysComparer : IEqualityComparer<OutputStep>
    {
        public bool Equals(OutputStep? x, OutputStep? y) =>
            x is PressKeys a && y is PressKeys b && a.Action == b.Action && a.Keys.SequenceEqual(b.Keys);

        public int GetHashCode(OutputStep obj) => 0;
    }
}
