namespace Ocr.Core.Pipeline;

internal interface IOcrPipelineRunner
{
    void ExecuteStage(
        OcrPipelineContext context,
        string stageName,
        Action stageAction,
        bool preserveOnFailure = false,
        Action<Exception>? onFailure = null,
        string? note = null);

    void RecordSkippedStage(OcrPipelineContext context, string stageName, string? note = null);
}
