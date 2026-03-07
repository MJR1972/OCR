using System.Diagnostics;

namespace Ocr.Core.Pipeline;

internal sealed class OcrPipelineRunner : IOcrPipelineRunner
{
    public void ExecuteStage(
        OcrPipelineContext context,
        string stageName,
        Action stageAction,
        bool preserveOnFailure = false,
        Action<Exception>? onFailure = null,
        string? note = null)
    {
        Debug.WriteLine($"[OCR Pipeline] Starting stage: {stageName}");
        var sw = Stopwatch.StartNew();
        var status = "completed";

        try
        {
            stageAction();
        }
        catch (Exception ex)
        {
            status = "failed";
            onFailure?.Invoke(ex);
            Debug.WriteLine($"[OCR Pipeline] Stage failed: {stageName} ({ex.GetType().Name}: {ex.Message})");

            if (!preserveOnFailure)
            {
                throw;
            }
        }
        finally
        {
            sw.Stop();
            context.StageTimings.Add(new PipelineStageTiming
            {
                StageName = stageName,
                DurationMs = (int)sw.ElapsedMilliseconds,
                Status = status,
                Note = note
            });
            Debug.WriteLine($"[OCR Pipeline] Completed stage: {stageName} in {sw.ElapsedMilliseconds} ms ({status})");
        }
    }

    public void RecordSkippedStage(OcrPipelineContext context, string stageName, string? note = null)
    {
        context.StageTimings.Add(new PipelineStageTiming
        {
            StageName = stageName,
            DurationMs = 0,
            Status = "skipped",
            Note = note
        });
        Debug.WriteLine($"[OCR Pipeline] Skipped stage: {stageName}. {note}");
    }
}
