using Ocr.Core.Contracts;

namespace Ocr.Core.Abstractions;

public interface IFieldRecognizer
{
    FieldRecognitionResult Recognize(IReadOnlyList<PageInfo> pages, double lowFieldThreshold);
}

public sealed class FieldRecognitionResult
{
    public List<RecognitionFieldInfo> Fields { get; init; } = [];
    public List<IssueInfo> Warnings { get; init; } = [];
    public Dictionary<int, int> PromotedByPage { get; init; } = [];
}
