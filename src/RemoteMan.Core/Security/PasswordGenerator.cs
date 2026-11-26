using System.Security.Cryptography;
using System.Text;

namespace RemoteMan.Core.Security;

public enum PasswordMode { Characters, Pronounceable, Words, Pattern, Hex }

/// <summary>What the password generator makes. A plain settings object so it can be saved as a profile.</summary>
public sealed class PasswordOptions
{
    public string Name { get; set; } = "Custom";
    public PasswordMode Mode { get; set; } = PasswordMode.Characters;

    // Characters
    public int Length { get; set; } = 20;
    public bool Upper { get; set; } = true;
    public bool Lower { get; set; } = true;
    public bool Digits { get; set; } = true;
    public bool Symbols { get; set; } = true;
    public bool Minus { get; set; }
    public bool Underline { get; set; }
    public bool Space { get; set; }
    public bool Brackets { get; set; }
    /// <summary>Least number of characters from each set (only for sets that are on).</summary>
    public int MinUpper { get; set; } = 1;
    public int MinLower { get; set; } = 1;
    public int MinDigits { get; set; } = 2;
    public int MinSymbols { get; set; } = 1;
    public int MinMinus { get; set; }
    public int MinUnderline { get; set; }
    public int MinSpace { get; set; }
    public int MinBrackets { get; set; }
    /// <summary>Extra characters to draw from.</summary>
    public string Custom { get; set; } = "";
    /// <summary>Leave out characters that look alike in many fonts (I l 1 O 0 o).</summary>
    public bool SkipLookAlikes { get; set; } = true;
    public bool NoRepeats { get; set; }
    public bool StartWithLetter { get; set; }
    /// <summary>Characters never to use, e.g. ones that break scripts or consoles.</summary>
    public string Exclude { get; set; } = "";

    // Pronounceable
    public int PronounceableLength { get; set; } = 16;
    public bool MixCapitals { get; set; } = true;
    public bool EndWithDigits { get; set; } = true;
    public bool EndWithSymbol { get; set; }

    // Hex
    public int HexLength { get; set; } = 32;
    public bool HexUpper { get; set; }

    // Words
    public int WordCount { get; set; } = 4;
    public string Separator { get; set; } = "-";
    public bool Capitalize { get; set; } = true;
    public bool AddNumber { get; set; } = true;

    // Pattern: A upper, a lower, 9 digit, ! symbol, h hex, * any, \x literal x; anything else is kept.
    public string Pattern { get; set; } = "Aaaa-9999-!!";

    public PasswordOptions Clone() => (PasswordOptions)MemberwiseClone();

    /// <summary>A short label for chips, e.g. "Strong 20".</summary>
    public string Summary => Mode switch
    {
        PasswordMode.Words => $"{WordCount} words",
        PasswordMode.Pattern => "Pattern",
        PasswordMode.Pronounceable => $"{PronounceableLength} chars",
        PasswordMode.Hex => $"{HexLength} hex",
        _ => $"{Length} chars",
    };
}

public readonly record struct GeneratedPassword(string Value, double Bits)
{
    public PasswordStrength Strength => PasswordGenerator.Rate(Bits);
}

public enum PasswordStrength { Weak, Fair, Strong, VeryStrong }

/// <summary>
/// Makes random passwords from the OS crypto random source (never <see cref="Random"/>).
/// Every pick is uniform: <see cref="RandomNumberGenerator.GetInt32(int)"/> rejects biased values.
/// </summary>
public static class PasswordGenerator
{
    public const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string Lower = "abcdefghijklmnopqrstuvwxyz";
    public const string Digits = "0123456789";
    public const string Symbols = "!#$%&*+=?@^";
    public const string Brackets = "()[]{}<>";
    public const string LookAlikes = "Il1O0o";
    public const int MinLength = 4, MaxLength = 128;

    /// <summary>Built-in profiles, in the order the chips show them.</summary>
    public static IReadOnlyList<PasswordOptions> BuiltIn { get; } =
    [
        new() { Name = "Strong" },
        new() { Name = "Windows / AD", Length = 16, MinDigits = 1, StartWithLetter = true },
        new() { Name = "Linux root", Length = 24, MinSymbols = 2, Exclude = "\\`'\"$" },
        new() { Name = "Network gear", Length = 18, Symbols = false, MinSymbols = 0, StartWithLetter = true },
        new() { Name = "PIN", Length = 6, Upper = false, Lower = false, Symbols = false, MinUpper = 0, MinLower = 0, MinSymbols = 0, MinDigits = 0, SkipLookAlikes = false, NoRepeats = true },
        new() { Name = "Passphrase", Mode = PasswordMode.Words },
        new() { Name = "Hex key", Mode = PasswordMode.Hex },
    ];

    /// <summary>The character sets that <paramref name="o"/> draws from with their minimums, after exclusions.
    /// Sets left empty by the exclusions are dropped. Custom characters come last, with no minimum.</summary>
    public static List<(string Chars, int Min)> CharacterSets(PasswordOptions o)
    {
        var skip = new HashSet<char>(o.Exclude ?? "");
        if (o.SkipLookAlikes) foreach (var c in LookAlikes) skip.Add(c);
        string F(string s) => new(s.Where(c => !skip.Contains(c)).Distinct().ToArray());
        var sets = new List<(string, int)>();
        void Add(bool on, string chars, int min) { if (on && F(chars) is { Length: > 0 } f) sets.Add((f, Math.Max(0, min))); }
        Add(o.Upper, Upper, o.MinUpper);
        Add(o.Lower, Lower, o.MinLower);
        Add(o.Digits, Digits, o.MinDigits);
        Add(o.Symbols, Symbols, o.MinSymbols);
        Add(o.Minus, "-", o.MinMinus);
        Add(o.Underline, "_", o.MinUnderline);
        Add(o.Space, " ", o.MinSpace);
        Add(o.Brackets, Brackets, o.MinBrackets);
        Add(true, o.Custom ?? "", 0);
        return sets;
    }

    /// <summary>Several candidates to choose from.</summary>
    public static List<GeneratedPassword> Batch(PasswordOptions o, int count) =>
        Enumerable.Range(0, count).Select(_ => Generate(o)).ToList();

    /// <summary>One password. Returns an empty value when the options allow no characters at all.</summary>
    public static GeneratedPassword Generate(PasswordOptions o) => o.Mode switch
    {
        PasswordMode.Words => Words(o),
        PasswordMode.Pronounceable => Pronounceable(o),
        PasswordMode.Hex => Hex(o),
        PasswordMode.Pattern => FromPattern(o.Pattern ?? ""),
        _ => Characters(o),
    };

    private static GeneratedPassword Characters(PasswordOptions o)
    {
        var sets = CharacterSets(o);
        if (sets.Count == 0) return new("", 0);
        var all = new string(sets.SelectMany(s => s.Chars).Distinct().ToArray());
        var n = Math.Clamp(o.Length, MinLength, MaxLength);
        var noRepeats = o.NoRepeats && all.Length >= n;   // can't avoid repeats when the pool is smaller than the length
        var used = new HashSet<char>();
        char Pick(string pool)
        {
            var free = noRepeats ? new string(pool.Where(c => !used.Contains(c)).ToArray()) : pool;
            if (free.Length == 0) free = pool;
            var c = free[RandomNumberGenerator.GetInt32(free.Length)];
            used.Add(c);
            return c;
        }

        // The minimums first, then fill up from everything, then shuffle so the minimums land anywhere.
        // This meets the rules without retry loops; minimums that add up past the length are cut short.
        var chars = new List<char>(n);
        foreach (var (set, min) in sets)
            for (var i = 0; i < min && chars.Count < n; i++) chars.Add(Pick(set));
        while (chars.Count < n) chars.Add(Pick(all));
        Shuffle(chars);

        if (o.StartWithLetter && !char.IsAsciiLetter(chars[0]))
        {
            var i = chars.FindIndex(char.IsAsciiLetter);
            if (i > 0) (chars[0], chars[i]) = (chars[i], chars[0]);
            else if (all.Where(char.IsAsciiLetter).ToArray() is { Length: > 0 } letters) chars[0] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
        }

        return new(new string(chars.ToArray()), n * Math.Log2(all.Length));
    }

    private const string Consonants = "bdfghjkmnprstvz", Vowels = "aeiu";

    /// <summary>Alternating consonants and vowels ("kavotemu"), easy to read out or type on a console.</summary>
    private static GeneratedPassword Pronounceable(PasswordOptions o)
    {
        var n = Math.Clamp(o.PronounceableLength, 6, MaxLength);
        var tail = new StringBuilder();
        double bits = 0;
        if (o.EndWithDigits) { tail.Append(RandomNumberGenerator.GetInt32(100).ToString("00")); bits += Math.Log2(100); }
        if (o.EndWithSymbol) { tail.Append(Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)]); bits += Math.Log2(Symbols.Length); }
        var sb = new StringBuilder(n);
        while (sb.Length < n - tail.Length)
        {
            var pool = sb.Length % 2 == 0 ? Consonants : Vowels;
            var c = pool[RandomNumberGenerator.GetInt32(pool.Length)];
            bits += Math.Log2(pool.Length);
            if (o.MixCapitals) { if (RandomNumberGenerator.GetInt32(2) == 0) c = char.ToUpperInvariant(c); bits += 1; }
            sb.Append(c);
        }
        return new(sb.Append(tail).ToString(), bits);
    }

    private static GeneratedPassword Hex(PasswordOptions o)
    {
        var n = Math.Clamp(o.HexLength, 8, 256);
        var digits = o.HexUpper ? "0123456789ABCDEF" : "0123456789abcdef";
        var chars = new char[n];
        for (var i = 0; i < n; i++) chars[i] = digits[RandomNumberGenerator.GetInt32(16)];
        return new(new string(chars), n * 4);
    }

    private static GeneratedPassword Words(PasswordOptions o)
    {
        var n = Math.Clamp(o.WordCount, 2, 12);
        var words = new List<string>(n + 1);
        for (var i = 0; i < n; i++)
        {
            var w = WordList[RandomNumberGenerator.GetInt32(WordList.Length)];
            words.Add(o.Capitalize ? char.ToUpperInvariant(w[0]) + w[1..] : w);
        }
        var bits = n * Math.Log2(WordList.Length);
        if (o.AddNumber) { words.Add(RandomNumberGenerator.GetInt32(100).ToString()); bits += Math.Log2(100); }
        return new(string.Join(o.Separator ?? "", words), bits);
    }

    private static GeneratedPassword FromPattern(string pattern)
    {
        var sb = new StringBuilder(pattern.Length);
        double bits = 0;
        string? Class(char c) => c switch
        {
            'A' => "ABCDEFGHJKLMNPQRSTUVWXYZ",
            'a' => "abcdefghijkmnpqrstuvwxyz",
            '9' => "23456789",
            '!' => Symbols,
            'h' => "0123456789abcdef",
            '*' => "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789" + Symbols,
            _ => null,
        };
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) { sb.Append(pattern[++i]); continue; }
            if (Class(c) is { } pool) { sb.Append(pool[RandomNumberGenerator.GetInt32(pool.Length)]); bits += Math.Log2(pool.Length); }
            else sb.Append(c);
        }
        return new(sb.ToString(), bits);
    }

    public static PasswordStrength Rate(double bits) => bits switch
    {
        < 45 => PasswordStrength.Weak,
        < 70 => PasswordStrength.Fair,
        < 100 => PasswordStrength.Strong,
        _ => PasswordStrength.VeryStrong,
    };

    public static string Label(PasswordStrength s) => s switch
    {
        PasswordStrength.Weak => "Weak",
        PasswordStrength.Fair => "Fair",
        PasswordStrength.Strong => "Strong",
        _ => "Very strong",
    };

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>256 short, distinct, easy-to-type words: 8 bits each.</summary>
    public static readonly string[] WordList =
    [
        "acorn", "actor", "adobe", "agent", "alarm", "album", "alpha", "amber", "anchor", "angle", "apple", "apron", "arena", "armor", "arrow", "aspen",
        "atlas", "atom", "attic", "audio", "badge", "bagel", "baker", "balsa", "bamboo", "banjo", "barley", "basil", "basin", "beach", "beacon", "beam",
        "berry", "birch", "bison", "blade", "blaze", "bloom", "board", "bolt", "bonus", "boulder", "brass", "bread", "breeze", "brick", "bridge", "brook",
        "brush", "bubble", "bucket", "buffalo", "bugle", "cabin", "cable", "cactus", "camel", "candle", "canoe", "canyon", "carbon", "cargo", "carpet", "castle",
        "cedar", "cello", "chalk", "cherry", "chess", "chief", "cider", "cinder", "circus", "citrus", "clam", "cliff", "clock", "cloud", "clover", "cobalt",
        "cocoa", "comet", "compass", "copper", "coral", "cotton", "cougar", "crane", "crater", "crayon", "creek", "cricket", "crown", "crystal", "cubic", "dagger",
        "daisy", "dawn", "delta", "denim", "desert", "diesel", "dingo", "disco", "dolphin", "domino", "dragon", "drum", "dune", "eagle", "echo", "eclipse",
        "elbow", "ember", "engine", "falcon", "feather", "fern", "fiddle", "fjord", "flame", "flint", "flute", "forest", "forge", "fossil", "fox", "frost",
        "galaxy", "garden", "garnet", "gecko", "geyser", "ginger", "glacier", "globe", "granite", "grape", "gravel", "guitar", "harbor", "hazel", "helmet", "heron",
        "hollow", "honey", "horizon", "husky", "igloo", "indigo", "iris", "iron", "island", "ivory", "jacket", "jade", "jaguar", "jasper", "jelly", "jungle",
        "juniper", "kayak", "kernel", "kettle", "kiwi", "koala", "lagoon", "lantern", "laser", "lemon", "lilac", "linen", "lizard", "lotus", "lunar", "magnet",
        "mango", "maple", "marble", "meadow", "melon", "mesa", "meteor", "mint", "mirror", "mosaic", "motor", "nectar", "nebula", "needle", "nickel", "nimbus",
        "noodle", "north", "nugget", "oasis", "ocean", "olive", "onyx", "orbit", "orchid", "otter", "oxide", "paddle", "panda", "parrot", "pebble", "pepper",
        "piano", "pine", "pixel", "planet", "plaza", "polar", "prism", "pulse", "quartz", "quill", "rabbit", "radar", "raven", "reef", "ridge", "river",
        "robin", "rocket", "saffron", "sage", "salmon", "satin", "shadow", "signal", "silver", "slate", "sonic", "spruce", "stone", "storm", "summit", "tango",
        "thistle", "thunder", "tiger", "timber", "topaz", "tulip", "tundra", "turbo", "umber", "valley", "velvet", "violet", "walnut", "willow", "winter", "zephyr",
    ];
}
