using Ocr.Core.Contracts;

namespace Ocr.Core.Abstractions;

public interface ITokenCleanupService
{
    TokenCleanupResult Cleanup(IReadOnlyList<TokenInfo> tokens, IReadOnlyList<RegionInfo> regions);
}

public sealed class TokenCleanupResult
{
    public HashSet<string> SkipTokenIds { get; init; } = [];
    public Dictionary<string, string> ReconstructedTextOverrides { get; init; } = [];
    public int TokensOriginal { get; init; }
    public int TokensModified { get; init; }
    public int TokensRemoved { get; init; }
    public int TokensSplit { get; init; }
    public int CheckboxArtifactsRemoved { get; init; }
    public int UnderlineArtifactsRemoved { get; init; }
    public int DictionaryCorrections { get; init; }
}
