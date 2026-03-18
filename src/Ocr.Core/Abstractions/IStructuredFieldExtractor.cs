using Ocr.Core.Contracts;

namespace Ocr.Core.Abstractions;

public interface IStructuredFieldExtractor
{
    StructuredFieldExtractionResult Extract(
        IReadOnlyList<PageInfo> pages,
        IReadOnlyList<RecognitionFieldInfo> existingFields,
        double lowFieldThreshold);
}

public sealed class StructuredFieldExtractionResult
{
    public Dictionary<int, List<KeyValueCandidateInfo>> AdditionalKeyValueCandidatesByPage { get; init; } = [];
    public List<RecognitionFieldInfo> AdditionalFields { get; init; } = [];
    public List<IssueInfo> Warnings { get; init; } = [];
    public int KeyValueCandidateCount { get; init; }
    public int PromotedFieldCount { get; init; }
    public int CheckboxDerivedFieldCount { get; init; }
}
