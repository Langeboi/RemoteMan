using RemoteMan.Core.Security;
using Xunit;

namespace RemoteMan.Tests;

public class PasswordGeneratorTests
{
    [Fact]
    public void Default_meets_length_and_minimums()
    {
        var o = new PasswordOptions { Length = 20, MinUpper = 2, MinLower = 2, MinDigits = 3, MinSymbols = 2 };
        for (var i = 0; i < 200; i++)
        {
            var pw = PasswordGenerator.Generate(o).Value;
            Assert.Equal(20, pw.Length);
            Assert.True(pw.Count(char.IsAsciiLetterUpper) >= 2, pw);
            Assert.True(pw.Count(char.IsAsciiLetterLower) >= 2, pw);
            Assert.True(pw.Count(char.IsAsciiDigit) >= 3, pw);
            Assert.True(pw.Count(PasswordGenerator.Symbols.Contains) >= 2, pw);
        }
    }

    [Fact]
    public void Skips_look_alikes_and_excluded_characters()
    {
        var o = new PasswordOptions { Length = 64, SkipLookAlikes = true, Exclude = "#$%" };
        for (var i = 0; i < 100; i++)
        {
            var pw = PasswordGenerator.Generate(o).Value;
            Assert.DoesNotContain(pw, c => PasswordGenerator.LookAlikes.Contains(c) || "#$%".Contains(c));
        }
    }

    [Fact]
    public void Only_uses_chosen_sets_plus_custom()
    {
        var o = new PasswordOptions { Upper = false, Lower = false, Symbols = false, MinUpper = 0, MinLower = 0, MinSymbols = 0, Minus = true, MinMinus = 1, Custom = "xy", SkipLookAlikes = false };
        for (var i = 0; i < 100; i++)
        {
            var pw = PasswordGenerator.Generate(o).Value;
            Assert.All(pw, c => Assert.True(char.IsAsciiDigit(c) || c is '-' or 'x' or 'y', pw));
            Assert.Contains('-', pw);
        }
    }

    [Fact]
    public void No_repeats_and_start_with_letter()
    {
        var o = new PasswordOptions { Length = 30, NoRepeats = true, StartWithLetter = true };
        for (var i = 0; i < 200; i++)
        {
            var pw = PasswordGenerator.Generate(o).Value;
            Assert.Equal(pw.Length, pw.Distinct().Count());
            Assert.True(char.IsAsciiLetter(pw[0]), pw);
        }
    }

    [Fact]
    public void Nothing_chosen_gives_empty()
    {
        var o = new PasswordOptions { Upper = false, Lower = false, Digits = false, Symbols = false };
        Assert.Equal("", PasswordGenerator.Generate(o).Value);
    }

    [Fact]
    public void Minimums_beyond_length_are_cut_short()
    {
        var o = new PasswordOptions { Length = 4, MinUpper = 4, MinLower = 4, MinDigits = 4, MinSymbols = 4 };
        Assert.Equal(4, PasswordGenerator.Generate(o).Value.Length);
    }

    [Fact]
    public void Pattern_keeps_literals_and_fills_classes()
    {
        var pw = PasswordGenerator.Generate(new PasswordOptions { Mode = PasswordMode.Pattern, Pattern = @"Lab-\A9999-hh" }).Value;
        Assert.Matches(@"^Lab-A[2-9]{4}-[0-9a-f]{2}$", pw);
    }

    [Fact]
    public void Words_hex_and_pronounceable()
    {
        var words = PasswordGenerator.Generate(new PasswordOptions { Mode = PasswordMode.Words, WordCount = 4, Separator = "-", AddNumber = true }).Value;
        Assert.Equal(5, words.Split('-').Length);

        var hex = PasswordGenerator.Generate(new PasswordOptions { Mode = PasswordMode.Hex, HexLength = 40 });
        Assert.Matches("^[0-9a-f]{40}$", hex.Value);
        Assert.Equal(160, hex.Bits);

        var pron = PasswordGenerator.Generate(new PasswordOptions { Mode = PasswordMode.Pronounceable, PronounceableLength = 16, EndWithDigits = true }).Value;
        Assert.Equal(16, pron.Length);
        Assert.Matches("[0-9]{2}$", pron);
    }

    [Fact]
    public void Batch_gives_distinct_candidates_and_word_list_is_unique()
    {
        var batch = PasswordGenerator.Batch(new PasswordOptions(), 10);
        Assert.Equal(10, batch.Select(b => b.Value).Distinct().Count());
        Assert.Equal(256, PasswordGenerator.WordList.Distinct().Count());
        Assert.Equal(PasswordStrength.VeryStrong, batch[0].Strength);
    }

    [Fact]
    public void Built_in_templates_all_generate()
    {
        foreach (var t in PasswordGenerator.BuiltIn)
            Assert.False(string.IsNullOrEmpty(PasswordGenerator.Generate(t).Value), t.Name);
    }
}
