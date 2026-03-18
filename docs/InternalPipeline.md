# Internal Pipeline Notes (Stability-First Refactor)

This document summarizes the non-breaking internal pipeline refactor in `Ocr.Core`.

## Public API Stability
- Public entry point remains `OcrProcessor.ProcessFile(string, OcrOptions?, CancellationToken)`.
- JSON contract/output shape remains stable while behavior has been enriched additively.
- WPF harness continues to act as a viewer/debugger only.

## Internal Pipeline Components
- `Ocr.Core.Pipeline/OcrPipelineContext.cs`
  - Per-run internal context for state handoff across stages.
  - Carries core run information (`filePath`, `options`, `root`, tessdata path, output folder), plus internal `Items`.
- `Ocr.Core.Pipeline/IOcrPipelineRunner.cs`
  - Internal stage orchestration abstraction.
- `Ocr.Core.Pipeline/OcrPipelineRunner.cs`
  - Executes named stages with timing and safe stage-failure handling for additive stages.
  - Records stage timing metadata (`stageName`, `durationMs`, `status`, `note`) in pipeline context.

## Stage Order
The orchestrator now executes the existing logic under explicit stages:
1. Input/Load
2. Render
3. Preprocess (per page)
4. OCR Extraction (per page)
5. Token Cleanup (tracked stage marker; cleanup remains in existing layout path)
6. Line Reconstruction (tracked stage marker; reconstruction remains in existing layout path)
7. Layout Analysis (per page)
8. Table Detection (per page)
9. Region Detection (per page)
10. Structured Field Extraction (per page key-value + document-level recognition/additive extraction)
11. Final Result Assembly

## Raw OCR Evidence Preservation
Raw evidence remains fully populated and unchanged in contract structure:
- `pages[].tokens`
- `pages[].lines`
- `pages[].blocks`
- `pages[].tables`
- `pages[].regions`
- `pages[].text.fullText`
- existing IDs, bbox values, confidences

Structured extraction remains additive (`keyValueCandidates`, `recognition.fields`, `extensions` diagnostics).

Current additive behavior also includes:
- token cleanup before final line reconstruction, including merged-word spacing repair for common OCR glue artifacts
- multiline table cell preservation in `pages[].tables[].cells[].text`
- multiline row-value preservation in `pages[].tables[].rows[].values` for string cells

## Defensive Failure Behavior
- Additive structured extraction stage is now isolated with safe failure handling:
  - On failure, raw OCR evidence and prior stages are preserved.
  - A warning is emitted (`structured_extraction_stage_failed`).

## Stage Timing Observability
- Stage timings are captured internally by `OcrPipelineRunner`.
- They are exposed additively through:
  - `extensions.pipelineStageTimings[]`
- Existing metrics are preserved unchanged (`metrics.totalMs`, `metrics.documentOcrMs`, `metrics.pagesMs`, `metrics.breakdownMs`).
- WPF harness reads and logs these timings in the Log tab as viewer-only diagnostics.

## Performance Safety
- OCR is still performed once per page.
- Render/preprocess/OCR/table/region stages continue to reuse page artifacts without duplicate expensive work.
- Table refinement may re-read cropped table cell regions for better cell content, but the primary page OCR pass still occurs once per page.
