using Ocr.Core.Contracts;

namespace Ocr.Core.Abstractions;

public interface IKeyValueExtractor
{
    KeyValueExtractionResult Extract(PageInfo page);
}

public sealed class KeyValueExtractionResult
{
    public List<KeyValueCandidateInfo> Candidates { get; init; } = [];
    public FieldExtractionDiagnosticsInfo Diagnostics { get; init; } = new();
    public List<IssueInfo> Warnings { get; init; } = [];
}
