# OCR DLL Integration Guide

## 1. Overview

- The OCR DLL (`Ocr.Core`) provides end-to-end OCR processing for PDF/image files and returns a JSON result containing raw OCR evidence plus additive structured interpretation.
- It provides:
  - file validation/loading/rendering
  - preprocessing and OCR execution
  - layout reconstruction (tokens/lines/blocks/full text)
  - table detection, region detection, key-value extraction, field recognition
  - metrics, warnings/errors, and optional debug artifacts
- Typical usage scenarios:
  - desktop app OCR processing
  - backend/API document processing
  - downstream automation using fields/tables/words
- The WPF harness is a consumer/debug viewer. OCR intelligence is implemented in the DLL.

## 2. Project / Runtime Requirements

Current integration requirements from implementation:

- Target framework: `.NET 10` (`net10.0`).
- Platform/runtime considerations:
  - `OpenCvSharp4.runtime.win` package is referenced (Windows runtime dependency).
  - PDF rendering uses `Ghostscript.NET` (Ghostscript runtime availability is required for PDF path).
  - OCR uses `Tesseract` package.
- Required package/runtime assumptions in `Ocr.Core.csproj`:
  - `Ghostscript.NET`
  - `Newtonsoft.Json`
  - `OpenCvSharp4`
  - `OpenCvSharp4.runtime.win`
  - `Tesseract`
- `tessdata` expectation:
  - DLL validates `Path.Combine(AppContext.BaseDirectory, "tessdata")`.
  - Requested language files (for example `eng.traineddata`) must exist there.
  - Missing tessdata/traineddata is returned as result-level errors.
- Output folder behavior:
  - If JSON/artifacts are enabled, DLL creates run folder under configured output folder or `AppContext.BaseDirectory/output`.

## 3. Referencing the DLL

Recommended integration patterns:

- Project reference (same solution/repo):

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\Ocr.Core\Ocr.Core.csproj" />
</ItemGroup>
```

- Assembly/package-style usage:
  - Reference built `Ocr.Core.dll` and ensure runtime/native dependencies are deployed.
  - Ensure `tessdata` is deployed under consuming app base directory.

## 4. Public Entry Points

### `IOcrProcessor`

Namespace: `Ocr.Core.Abstractions`

```csharp
public interface IOcrProcessor
{
    OcrResult ProcessFile(string filePath, OcrOptions? options = null, CancellationToken ct = default);
}
```

- Purpose: main OCR entry point abstraction for consumers/DI.
- Parameters:
  - `filePath` (required): input file path.
  - `options` (optional): OCR behavior/output settings.
  - `ct` (optional): cancellation token.
- Return type: `OcrResult`.

### `OcrProcessor`

Namespace: `Ocr.Core.Services`

- Concrete implementation of `IOcrProcessor`.
- Default constructor provides built-in detector/extractor pipeline.
- Additional constructors allow injecting table/region/line/cleanup/key-value/field/structured services.
- Public method:

```csharp
public OcrResult ProcessFile(string filePath, OcrOptions? options = null, CancellationToken ct = default)
```

Expected behavior:

- Validates input and tessdata.
- Processes pages and builds JSON contract.
- Returns `OcrResult` even for many failure conditions (errors/warnings in JSON).

## 5. Main Request / Options Models

### `OcrOptions` (`Ocr.Core.Models`)

Represents run-time OCR settings.

Key properties:

- Core OCR:
  - `TargetDpi` (default `300`)
  - `Language` (default `"eng"`, supports multi-language separators)
  - `PageSegMode`, `EngineMode` (nullable int values mapped to Tesseract enums)
  - `PreserveInterwordSpaces`
- Preprocessing:
  - `EnableDeskew`, `MaxDeskewDegrees`, `DeskewAngleStep`, `MinDeskewConfidence`
  - `EnableDenoise`, `DenoiseMethod`, `DenoiseKernel`
  - `EnableBinarization`, `BinarizationMethod`
  - `EnableContrastEnhancement`, `ContrastMethod`
- Output/debug:
  - `SaveJsonToDisk`
  - `OutputFolder`
  - `SaveDebugArtifacts`
  - `SaveTokenOverlay`
  - `EnableNoiseFiltering`
- Misc:
  - `ProfileName`

Notes:

- `options` is optional in `ProcessFile`.
- DLL normalizes several values internally (for example fallback DPI/language, kernel/limits).

### `OcrRequest` (`Ocr.Core.Models`)

```csharp
public sealed record OcrRequest(string FilePath);
```

- Exists as a model type but is not the public `ProcessFile` method input today.

## 6. How to Call the DLL

### Basic usage (default pipeline)

```csharp
using Ocr.Core.Models;
using Ocr.Core.Services;

var processor = new OcrProcessor();

var options = new OcrOptions
{
    Language = "eng",
    TargetDpi = 300,
    SaveJsonToDisk = true,
    SaveDebugArtifacts = false,
    SaveTokenOverlay = false
};

var result = processor.ProcessFile(@"C:\docs\sample.pdf", options);

string json = result.Json;
string? outputPath = result.OutputJsonPath;
```

### Via abstraction (`IOcrProcessor`)

```csharp
using Ocr.Core.Abstractions;
using Ocr.Core.Models;

IOcrProcessor processor = new Ocr.Core.Services.OcrProcessor();
var result = processor.ProcessFile(filePath, new OcrOptions(), cancellationToken);
```

## 7. Result Model / Return Value

### `OcrResult` (`Ocr.Core.Models`)

```csharp
public sealed class OcrResult
{
    public string Json { get; init; }
    public string? OutputJsonPath { get; init; }
}
```

- `Json`: full OCR contract payload as JSON string.
- `OutputJsonPath`: path to written `result.json` when disk output is enabled and successful.

Major JSON sections a consumer can expect (see `OcrContractRoot`):

- document metadata (`document`)
- definitions/schema (`schema`, `definitions`)
- metrics (`metrics`)
- pages (`pages`)
  - tokens, lines, blocks, full text
  - tables, regions, keyValueCandidates, pageWords
- recognition (`recognition.fields` etc.)
- issues (`warnings`, `errors`)
- extensions diagnostics (`extensions`)
- document word list (`documentWords`)

Important behavior:

- Raw OCR evidence layer is preserved.
- Structured extraction is additive on top of raw evidence.

## 8. Raw OCR Evidence Reference

Where to read raw evidence:

- `pages[].tokens[]`
  - OCR token text, confidence, raw confidence, bbox, ids.
- `pages[].lines[]`
  - token grouping into line structures with bbox/confidence.
- `pages[].blocks[]`
  - line grouping into block structures.
- `pages[].text.fullText`
  - reconstructed page-level text.
- `pages[].size`, `pages[].quality`, `pages[].timing`
  - context for dimensions, confidence quality, page timings.

Typical uses:

- custom extraction rules
- custom confidence/review workflows
- visual mapping and annotation (bbox)
- auditability and explainability of derived fields

## 9. Tables / Regions / Words / Structured Fields

### Tables

- Path: `pages[].tables[]`
- Represents detected tables with:
  - detection metadata
  - header/rows/cells
  - confidence and token coverage
- Use cases:
  - export table rows to CSV/DB
  - confidence-based review routing

### Regions (checkbox/radio)

- Path: `pages[].regions[]`
- Represents detected UI-style controls with bbox/confidence/value/label token ids.
- Use cases:
  - form-state extraction
  - downstream boolean/option mapping

### Unique words

- Document-level: `documentWords[]`
- Page-level: `pages[].pageWords[]`
- Use cases:
  - indexing/search
  - quick vocabulary checks

### Key-value candidates

- Path: `pages[].keyValueCandidates[]`
- Represents heuristic label/value pairs with confidence, bbox, token ids.

### Structured fields

- Path: `recognition.fields[]`
- Represents promoted, normalized fields with source/validation/review metadata.

### Metrics/timing

- Path: `metrics`, `pages[].timing`, `extensions.pipelineStageTimings[]`
- Use cases:
  - performance monitoring
  - regression detection

## 10. JSON Serialization / Output

Current behavior:

- `ProcessFile` returns JSON as `OcrResult.Json` (always).
- If `SaveJsonToDisk = true`, DLL writes `result.json` and returns path in `OutputJsonPath`.
- JSON uses camelCase naming and includes null values (Newtonsoft settings in DLL).

Consumer deserialization pattern:

```csharp
using Newtonsoft.Json;
using Ocr.Core.Contracts;

var root = JsonConvert.DeserializeObject<OcrContractRoot>(result.Json);
if (root is null)
{
    // Handle invalid/unexpected JSON payload
}
```

## 11. Error Handling / Warnings / Diagnostics

Recommended handling:

- Handle invocation-level exceptions in consumer code (defensive).
- Always inspect result JSON `errors[]` and `warnings[]`.
- Treat `errors[]` as run/result errors (for example invalid input, tessdata missing, render failures).
- Treat `warnings[]` as partial-quality/diagnostic signals.
- Partial success is possible:
  - some pages/stages may fail while output still contains usable raw evidence.
- Additive structured stage has preservation behavior:
  - if that stage fails, warning is recorded and raw OCR evidence is retained.

## 12. Metrics and Performance Information

Where to read timing/diagnostics:

- `metrics.totalMs`
- `metrics.documentOcrMs`
- `metrics.pagesMs[]`
- `metrics.breakdownMs` (render/preprocess/ocr/layout/tableDetect/recognition/postprocess)
- `pages[].timing`
- `extensions.pipelineStageTimings[]` (stage name, duration, status, note)
- Additional diagnostics in `extensions`:
  - preprocessing profiles
  - debug artifact paths
  - noise diagnostics
  - line reconstruction diagnostics
  - token cleanup stats
  - field extraction diagnostics

## 13. Example Consumer Scenarios

### Desktop app integration

- Use `OcrProcessor` directly from UI-triggered command.
- Bind summary, pages, words, tables from deserialized `OcrContractRoot`.

### API/service integration

- Wrap `ProcessFile` in application service endpoint.
- Return JSON directly or mapped DTOs.
- Persist `result.Json` and selected derived data.

### Custom automation examples

- Custom rules over `pages[].tokens` for domain extraction.
- Use `recognition.fields` for quick automation fields.
- Use `pages[].tables` for tabular export workflows.
- Use `documentWords`/`pageWords` for indexing.

## 14. Architectural Guidance for Consumers

Recommended usage rules:

- Treat raw OCR evidence (`tokens/lines/blocks/fullText`) as source of truth.
- Treat structured extraction as additive interpretation.
- Do not assume every section is always populated.
- Always check `warnings[]` and `errors[]`.
- Use confidence and review/validation metadata for downstream trust decisions.
- Keep OCR logic in service/domain layers (not UI-only layers).

## 15. Developer Notes / Extension Considerations

Current strengths:

- Rich evidence-first JSON contract.
- Built-in tables/regions/structured extraction and diagnostics.
- Single-call entry point for integration.

Current limitations/considerations:

- File-path input API only (`ProcessFile`).
- Runtime/deployment dependencies (tessdata + native/runtime packages).
- Some extraction results are heuristic/content-dependent.

Likely extension areas (without assuming current implementation):

- expanded document-type/classification workflows
- stronger domain-specific field normalization/validation
- service-hosting wrappers and batch orchestration

## 16. Summary

Integrate the OCR DLL by referencing `Ocr.Core`, creating `OcrProcessor` (or `IOcrProcessor`), and calling `ProcessFile(filePath, options, ct)`. The returned `OcrResult` provides JSON containing raw OCR evidence, additive structured outputs, warnings/errors, and detailed metrics/diagnostics that can be consumed by desktop or service applications.
