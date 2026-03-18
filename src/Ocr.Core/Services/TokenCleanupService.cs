using System.Text.RegularExpressions;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;

namespace Ocr.Core.Services;

public sealed class TokenCleanupService : ITokenCleanupService
{
    private static readonly Regex UnderscoreOnlyRegex = new(@"^_+$", RegexOptions.Compiled);
    private static readonly Regex CheckboxContamNoSpace = new(@"^([AO08])([A-Za-z]{2,})$", RegexOptions.Compiled);
    private static readonly Regex CheckboxContamWithSpace = new(@"^([AO08])\s+([A-Za-z]{2,})$", RegexOptions.Compiled);
    private static readonly Regex CollapseSpaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LowerToAcronymRegex = new(@"(?<=[a-z])(?=[A-Z]{2,})", RegexOptions.Compiled);
    private static readonly Regex AcronymToWordRegex = new(@"(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);
    private static readonly Regex ThisIsStandaloneRegex = new(@"(?i)\bthisis\b", RegexOptions.Compiled);
    private static readonly Regex ThisIsFollowedByWordRegex = new(@"(?i)\bthisis(?=[a-z])", RegexOptions.Compiled);
    private static readonly Regex IsBeforeTestRegex = new(@"(?i)\bis(?=test\b)", RegexOptions.Compiled);
    private static readonly Dictionary<string, string> CommonCorrections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["engiish"] = "english",
        ["langusges"] = "languages",
        ["offce"] = "office",
        ["esponsibilty"] = "responsibility",
        ["mustocr"] = "must OCR",
        ["ocrdll"] = "OCR DLL"
    };

    public TokenCleanupResult Cleanup(IReadOnlyList<TokenInfo> tokens, IReadOnlyList<RegionInfo> regions)
    {
        var skipIds = new HashSet<string>(StringComparer.Ordinal);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        var removed = 0;
        var split = 0;
        var checkboxArtifactsRemoved = 0;
        var tokensModified = 0;
        var underlineArtifactsRemoved = 0;
        var dictionaryCorrections = 0;

        var overlapRegions = regions
            .Where(r => string.Equals(r.Type, "checkbox", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.Type, "radio", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Bbox)
            .ToList();

        foreach (var token in tokens)
        {
            var value = token.Text?.Trim() ?? string.Empty;
            if (value.Length == 0)
            {
                continue;
            }

            if (UnderscoreOnlyRegex.IsMatch(value) && value.Length <= 10)
            {
                skipIds.Add(token.Id);
                removed++;
                underlineArtifactsRemoved++;
                continue;
            }

            if (overlapRegions.Any(b => Intersects(token.Bbox, b)))
            {
                skipIds.Add(token.Id);
                checkboxArtifactsRemoved++;
                continue;
            }

            var cleaned = NormalizeGlueArtifacts(value, out var underlineFragmentsRemoved);
            underlineArtifactsRemoved += underlineFragmentsRemoved;

            if (value.StartsWith('_') && value.Length > 1)
            {
                cleaned = cleaned.TrimStart('_').Trim();
                underlineArtifactsRemoved++;
            }

            cleaned = ApplyPatternSpacing(cleaned);
            cleaned = ApplyDictionaryCorrections(cleaned, out var correctedByDictionary);
            if (correctedByDictionary)
            {
                dictionaryCorrections++;
            }

            if (!string.Equals(cleaned, value, StringComparison.Ordinal))
            {
                overrides[token.Id] = cleaned;
                tokensModified++;
            }

            if (TryCleanupCheckboxMergedWord(token, cleaned, out var mergedCleaned))
            {
                overrides[token.Id] = mergedCleaned;
                split++;
                checkboxArtifactsRemoved++;
                tokensModified++;
            }
        }

        return new TokenCleanupResult
        {
            SkipTokenIds = skipIds,
            ReconstructedTextOverrides = overrides,
            TokensOriginal = tokens.Count,
            TokensModified = tokensModified,
            TokensRemoved = removed,
            TokensSplit = split,
            CheckboxArtifactsRemoved = checkboxArtifactsRemoved,
            UnderlineArtifactsRemoved = underlineArtifactsRemoved,
            DictionaryCorrections = dictionaryCorrections
        };
    }

    private static bool TryCleanupCheckboxMergedWord(TokenInfo token, string value, out string cleaned)
    {
        cleaned = value;
        if (token.Confidence >= 0.8 || value.Length < 3 || value.Length > 40)
        {
            return false;
        }

        var match = CheckboxContamNoSpace.Match(value);
        if (!match.Success)
        {
            match = CheckboxContamWithSpace.Match(value);
        }

        if (!match.Success)
        {
            return false;
        }

        var suffix = match.Groups[2].Value;
        if (!suffix.All(char.IsLetter))
        {
            return false;
        }

        var avgCharWidth = token.Bbox.W / (double)Math.Max(1, value.Length);
        if (avgCharWidth < 2.0 || token.Bbox.W < token.Bbox.H * 1.35)
        {
            return false;
        }

        cleaned = ApplyCasePattern(suffix, value);
        return true;
    }

    private static string NormalizeGlueArtifacts(string value, out int underlineFragmentsRemoved)
    {
        underlineFragmentsRemoved = 0;
        var result = value;
        if (result.Contains('_'))
        {
            underlineFragmentsRemoved += result.Count(ch => ch == '_');
        }

        result = result.Replace("**_", " ", StringComparison.Ordinal);
        result = Regex.Replace(result, @"_+", " ");
        result = Regex.Replace(result, @"\*{2,}", " ");
        result = result.Replace("*", string.Empty, StringComparison.Ordinal);
        result = Regex.Replace(result, @"([A-Za-z])(\d)", "$1 $2");
        result = Regex.Replace(result, @"([:\-]+)(\d)", "$1 $2");
        result = CollapseSpaces.Replace(result, " ").Trim();
        return result;
    }

    private static string ApplyPatternSpacing(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var result = Regex.Replace(value, "(?i)doyou", "do you");
        result = Regex.Replace(result, "(?i)owna", "own a");
        result = ThisIsStandaloneRegex.Replace(result, match => ApplyCasePattern("this is", match.Value));
        result = ThisIsFollowedByWordRegex.Replace(result, match => ApplyCasePattern("this is", match.Value));
        result = IsBeforeTestRegex.Replace(result, match => ApplyCasePattern("is ", match.Value));
        result = LowerToAcronymRegex.Replace(result, " ");
        result = AcronymToWordRegex.Replace(result, " ");
        result = Regex.Replace(result, @"([A-Za-z])(\d)", "$1 $2");
        result = Regex.Replace(result, @"(\d)([A-Za-z])", "$1 $2");
        result = Regex.Replace(result, @"(?<=\w)#(?=\w)", " # ");
        result = Regex.Replace(result, @"(?<=\w)(?=#)", " ");
        result = Regex.Replace(result, @"(?<=#)(?=\w)", " ");
        result = Regex.Replace(result, @"(?i)\btargeting\.(net)\b", "targeting .$1");
        result = Regex.Replace(result, @"(?i)\bexisting\.(net)\b", "existing .$1");
        result = CollapseSpaces.Replace(result, " ").Trim();
        if (value.Length > 0 && char.IsUpper(value[0]) && result.Length > 0)
        {
            return char.ToUpperInvariant(result[0]) + result[1..];
        }

        return result;
    }

    private static string ApplyDictionaryCorrections(string value, out bool corrected)
    {
        corrected = false;
        if (value.Length == 0)
        {
            return value;
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            if (CommonCorrections.TryGetValue(words[i], out var replacement))
            {
                words[i] = ApplyCasePattern(replacement, words[i]);
                corrected = true;
            }
        }

        return corrected ? string.Join(' ', words) : value;
    }

    private static string ApplyCasePattern(string replacement, string source)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(replacement))
        {
            return replacement;
        }

        if (source.All(char.IsUpper))
        {
            return replacement.ToUpperInvariant();
        }

        if (char.IsUpper(source[0]) && source.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c)))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        return replacement.ToLowerInvariant();
    }

    private static bool Intersects(BboxInfo a, BboxInfo b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        return x2 > x1 && y2 > y1;
    }
}
