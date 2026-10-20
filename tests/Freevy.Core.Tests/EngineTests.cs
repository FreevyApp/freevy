using Freevy.Core;
using Freevy.Core.Import.Breevy;

namespace Freevy.Core.Tests;

public class EngineTests
{
    private static ExpansionEngine NewEngine() =>
        new(BvyParser.Parse(ParserTests.Fixture), BreevyPrefs.DefaultDelimiters);

    private static Expansion? TypeAll(ExpansionEngine engine, string text)
    {
        Expansion? last = null;
        foreach (var c in text)
        {
            last = engine.Type(c) ?? last;
        }

        return last;
    }

    [Fact]
    public void After_delimiter_snippet_fires_on_delimiter_and_keeps_it()
    {
        var result = TypeAll(NewEngine(), "teh ");

        Assert.NotNull(result);
        Assert.Equal("teh ", result.Deleted);
        Assert.Equal("the ", result.Text);
    }

    [Fact]
    public void After_delimiter_snippet_does_not_fire_without_delimiter()
    {
        Assert.Null(TypeAll(NewEngine(), "teh"));
    }

    [Fact]
    public void Word_start_flag_blocks_matches_inside_words()
    {
        Assert.Null(TypeAll(NewEngine(), "xteh "));
    }

    [Fact]
    public void Immediate_snippet_fires_without_delimiter()
    {
        var result = TypeAll(NewEngine(), "sevic");

        Assert.NotNull(result);
        Assert.Equal("sevic", result.Deleted);
        Assert.Equal("servic", result.Text);
    }

    [Fact]
    public void Immediate_snippet_without_word_start_fires_mid_word()
    {
        var result = TypeAll(NewEngine(), "abc;sig");

        Assert.NotNull(result);
        Assert.Equal(";sig", result.Deleted);
    }

    [Theory]
    [InlineData("Teh ", "The ")]
    [InlineData("TEH ", "THE ")]
    [InlineData("hte.", "the.")]
    public void Adapt_case_follows_typed_case(string typed, string expected)
    {
        Assert.Equal(expected, TypeAll(NewEngine(), typed)?.Text);
    }

    [Fact]
    public void Exact_case_requires_identical_abbreviation()
    {
        Assert.Null(TypeAll(NewEngine(), ";SIG"));
    }

    [Fact]
    public void Backspace_corrects_the_buffer()
    {
        var engine = NewEngine();
        TypeAll(engine, "tex");
        engine.Backspace();
        engine.Backspace();

        Assert.Equal("the ", TypeAll(engine, "eh ")?.Text);
    }

    [Fact]
    public void Reset_drops_context()
    {
        var engine = NewEngine();
        TypeAll(engine, "te");
        engine.Reset();

        Assert.Null(TypeAll(engine, "h "));
    }

    [Fact]
    public void Expansions_chain_with_correct_word_boundaries()
    {
        var engine = NewEngine();

        Assert.Equal("the ", TypeAll(engine, "teh ")?.Text);
        Assert.Equal("the ", TypeAll(engine, "teh ")?.Text);
    }

    [Fact]
    public void Long_typing_keeps_word_boundary_after_trimming()
    {
        var engine = NewEngine();

        Assert.Null(TypeAll(engine, "averyveryverylongwordteh "));
        Assert.Equal("the ", TypeAll(engine, "averyveryverylongword teh ")?.Text);
    }

    [Fact]
    public void Enter_is_a_delimiter()
    {
        Assert.Equal("the\n", TypeAll(NewEngine(), "teh\r")?.Text);
    }
}
