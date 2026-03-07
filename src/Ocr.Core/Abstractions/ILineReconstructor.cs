using Ocr.Core.Contracts;

namespace Ocr.Core.Abstractions;

public interface ILineReconstructor
{
    LineReconstructionResult Reconstruct(
        IReadOnlyList<TokenInfo> tokens,
        int pageIndex,
        int pageWidth,
        int pageHeight,
        double lowLineThreshold,
        IReadOnlySet<string>? skipTokenIds = null,
        IReadOnlyDictionary<string, string>? reconstructedTextOverrides = null);
}

public sealed class LineReconstructionResult
{
    public List<LineInfo> Lines { get; init; } = [];
    public Dictionary<string, string> LineTexts { get; init; } = [];
    public string FullText { get; init; } = string.Empty;
    public int TokensAssigned { get; init; }
    public bool Successful { get; init; }
}
