using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RandomScrambleWeb;

/// <summary>
/// Turns dictated text like "Corey eighty five Mike ninety two" into
/// name/score pairs, one per player.
///
/// Recognizers are inconsistent about numbers — Chrome usually hands back
/// "85" while the Windows engine says "eighty five" — so both spellings are
/// accepted. A name whose score hasn't been spoken yet comes back as
/// <see cref="Result.Leftover"/> rather than being dropped, so pausing between
/// the name and the number is harmless.
/// </summary>
public static class SpeechEntryParser
{
    public readonly record struct Entry(string Name, string ScoreText);

    public sealed class Result
    {
        public List<Entry> Entries { get; } = new();

        /// <summary>
        /// Trailing words with no score attached yet. Prepend this to the next
        /// phrase before parsing it.
        /// </summary>
        public string Leftover { get; set; } = "";
    }

    /// <summary>
    /// Words kept from a phrase that ended without a score. A name waiting on
    /// its number runs one or two words; anything longer is misheard noise, and
    /// gets dropped rather than glued onto the next name.
    /// </summary>
    private const int MaxLeftoverWords = 2;

    /// <summary>
    /// Words taken for a name, counting back from the score. Caps how much
    /// stray speech ahead of a name can land in the row.
    /// </summary>
    private const int MaxNameWords = 3;

    private static readonly Regex TokenPattern =
        new(@"\p{L}+(?:'\p{L}+)*|\d+(?:\.\d+)?", RegexOptions.Compiled);

    private static readonly Regex DigitToken =
        new(@"^\d+(?:\.\d+)?$", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Ones = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["oh"] = 0,
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
        ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40,
        ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80,
        ["ninety"] = 90,
    };

    /// <summary>
    /// Connective words people drop between a name and a score. Stripped so
    /// "Mike scored ninety" doesn't become a player called "Mike Scored".
    /// </summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "and", "with", "score", "scored", "scores", "shot", "shoots",
        "is", "was", "has", "had", "got", "gets", "then", "next",
        "plus", "comma", "dash",
    };

    public static Result Parse(string text)
    {
        var result = new Result();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var tokens = Tokenize(text);
        var nameWords = new List<string>();

        int i = 0;
        while (i < tokens.Count)
        {
            if (TryReadNumber(tokens, i, out var scoreText, out int consumed))
            {
                // A score with no name ahead of it has nothing to attach to
                if (nameWords.Count > 0)
                {
                    if (nameWords.Count > MaxNameWords)
                        nameWords.RemoveRange(0, nameWords.Count - MaxNameWords);

                    result.Entries.Add(new Entry(TitleCase(nameWords), scoreText));
                    nameWords.Clear();
                }

                i += consumed;
                continue;
            }

            if (!Fillers.Contains(tokens[i]))
                nameWords.Add(tokens[i]);

            i++;
        }

        result.Leftover = nameWords.Count <= MaxLeftoverWords
            ? string.Join(" ", nameWords)
            : "";

        return result;
    }

    private static List<string> Tokenize(string text) =>
        TokenPattern.Matches(text.ToLowerInvariant())
                    .Select(m => m.Value)
                    .ToList();

    /// <summary>
    /// Reads a score starting at <paramref name="start"/>. Returns false with
    /// <paramref name="consumed"/> at 0 when the tokens there aren't a number,
    /// so the caller can treat them as part of a name instead.
    /// </summary>
    private static bool TryReadNumber(List<string> tokens, int start,
                                      out string scoreText, out int consumed)
    {
        scoreText = "";
        consumed = 0;

        int i = start;
        bool negative = tokens[i] is "minus" or "negative";
        if (negative) i++;

        if (!TryReadWhole(tokens, ref i, out double value))
            return false;

        // "eighty point five"
        if (i < tokens.Count && tokens[i] == "point")
        {
            int beforePoint = i;
            i++;

            var fraction = ReadDigitRun(tokens, ref i);
            if (fraction.Length > 0)
                value += double.Parse("0." + fraction, CultureInfo.InvariantCulture);
            else
                i = beforePoint;
        }

        if (negative) value = -value;

        scoreText = FormatScore(value);
        consumed = i - start;
        return true;
    }

    /// <summary>Reads the integer part, either as digits or as number words.</summary>
    private static bool TryReadWhole(List<string> tokens, ref int i, out double value)
    {
        value = 0;

        // A digit token from the recognizer stands on its own — two of them in
        // a row are two different players' scores, not one number.
        if (i < tokens.Count && DigitToken.IsMatch(tokens[i]))
        {
            value = double.Parse(tokens[i], CultureInfo.InvariantCulture);
            i++;
            return true;
        }

        long total = 0, current = 0;
        bool any = false;

        while (i < tokens.Count)
        {
            var token = tokens[i];

            // "one hundred and five"
            if (token == "and" && any && i + 1 < tokens.Count && IsNumberWord(tokens[i + 1]))
            {
                i++;
                continue;
            }

            if (Ones.TryGetValue(token, out int one)) current += one;
            else if (Tens.TryGetValue(token, out int ten)) current += ten;
            else if (token == "hundred") current = (current == 0 ? 1 : current) * 100;
            else if (token == "thousand") { total += (current == 0 ? 1 : current) * 1000; current = 0; }
            else break;

            any = true;
            i++;
        }

        value = total + current;
        return any;
    }

    /// <summary>Digits after "point", spoken either way: "five" or "5".</summary>
    private static string ReadDigitRun(List<string> tokens, ref int i)
    {
        var digits = new StringBuilder();

        while (i < tokens.Count)
        {
            var token = tokens[i];

            if (DigitToken.IsMatch(token) && !token.Contains('.'))
                digits.Append(token);
            else if (Ones.TryGetValue(token, out int d) && d <= 9)
                digits.Append(d);
            else break;

            i++;
        }

        return digits.ToString();
    }

    private static bool IsNumberWord(string token) =>
        Ones.ContainsKey(token) || Tens.ContainsKey(token)
        || token is "hundred" or "thousand" || DigitToken.IsMatch(token);

    private static string FormatScore(double value) =>
        value % 1 == 0
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Capitalizes each word, apostrophes included — O'Brien.</summary>
    private static string TitleCase(List<string> words)
    {
        var cased = words.Select(word =>
        {
            var letters = word.ToCharArray();

            for (int i = 0; i < letters.Length; i++)
                if (i == 0 || letters[i - 1] == '\'')
                    letters[i] = char.ToUpperInvariant(letters[i]);

            return new string(letters);
        });

        return string.Join(" ", cased);
    }
}
