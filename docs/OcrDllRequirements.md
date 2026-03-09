# OCR DLL Requirements

## 1. Overview

- The OCR DLL (`Ocr.Core`) provides document OCR processing and emits a structured JSON contract for downstream use.
- The DLL performs page loading/rendering, preprocessing, OCR extraction, layout reconstruction, table/region detection, key-value extraction, and field recognition.
- The WPF test harness (`Ocr.TestHarness.Wpf`) is a consumer of the DLL. It configures options, invokes `IOcrProcessor.ProcessFile`, and visualizes JSON/artifacts for debugging.

## 2. Scope

Current DLL scope includes:

- File-path based input processing for PDFs and images.
- OCR extraction using Tesseract.
- JSON result generation (in-memory string and optional disk output).
- Layout construction: tokens, lines, blocks, reading-order text.
- Table detection and table structure output.
- Region detection (checkbox/radio style controls) and region output.
- Page-level and document-level unique word list generation.
- Heuristic structured extraction (`keyValueCandidates` and `recognition.fields`).
- Timing/metrics collection and warnings/errors collection.
- Optional debug artifacts (render/preprocess images and overlays).

## 3. Architectural Responsibilities

### OCR DLL Responsibilities

- Owns OCR/document intelligence logic and JSON contract assembly.
- Owns preprocessing, OCR execution, layout analysis, detection/extraction, and output serialization.
- Owns pipeline diagnostics, warnings/errors, and debug artifact generation.

### WPF Test Harness Responsibilities

- UI for selecting input/options and running the processor.
- Display/inspection of generated JSON, summary metrics, tables, words, and overlay artifacts.
- Logging and UX-only behaviors (zoom, preview tabs, table inspectors).

The OCR/document intelligence belongs in the DLL. The harness is a viewer/debug runner and must not become the system of record for OCR logic.

## 4. Supported Inputs

- Input model: single `filePath` string via `ProcessFile(string filePath, OcrOptions? options, CancellationToken ct)`.
- Supported extensions validated by DLL:
  - `.pdf`, `.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`, `.gif`, `.bmp`
- File type handling:
  - PDF: rendered page-by-page using Ghostscript rasterization at `TargetDpi`.
  - Image: loaded as a single page via OpenCV (`Cv2.ImRead`).
- MIME/fileType metadata are derived from extension and written into contract source metadata.
- If no pages are rendered/loaded, an error is recorded and processing finalizes with contract output.

## 5. OCR Processing Requirements

Current processing behavior is page-based:

- Rendering/loading:
  - PDF pages are rasterized; per-page render failures are captured as errors while processing continues for other pages.
  - Non-PDF image input yields one page.
- Preprocessing:
  - Grayscale conversion always occurs.
  - Deskew is enabled by default (option-controlled) using projection-variance angle search.
  - Optional denoise, binarization, and contrast enhancement are applied based on options.
- OCR execution:
  - Tesseract engine is created once per run and used for pages.
  - OCR token extraction runs once per page.
- Token extraction and confidence:
  - Word tokens include normalized confidence (`0..1`) and raw confidence.
  - Low-confidence flags are set using contract thresholds.
- Line reconstruction:
  - Initial line grouping uses OCR geometry.
  - Cleanup/reconstruction services refine line text and token assignment.
- Block grouping:
  - Lines are grouped into text blocks; block confidence is aggregated from lines.
- Reading order/full text:
  - Full text is generated in top-to-bottom, left-to-right line order.

## 6. Raw OCR Evidence Requirements

The raw OCR evidence layer is the source of truth and is preserved in output:

- `pages[].tokens[]`
- `pages[].lines[]`
- `pages[].blocks[]`
- `pages[].tables[]`
- `pages[].regions[]`
- `pages[].text.fullText`
- Bounding boxes (`bbox`) and confidence values at token/line/block/table/cell/field levels

Structured interpretation is additive and must not replace or remove this evidence layer in the contract response.

## 7. Table Detection Requirements

Current implementation uses a hybrid detector:

- Primary path: gridline detector (`method="lines"`) using morphological line extraction.
- Fallback path: layout detector (`method="layout"`) when gridline path yields no tables.
- Table output includes:
  - table id, confidence, bbox
  - detection metadata (`method`, `hasExplicitGridLines`)
  - grid bands (rows/columns)
  - header columns/cells
  - data cells and normalized values
  - row objects with keyed values
  - token coverage metrics and detection issues/warnings
- Optional table overlay artifact is generated when debug artifacts are enabled.

## 8. Region Detection Requirements

Current region detection is implemented for checkbox/radio-like controls:

- Candidate generation from contours plus Hough-circle candidates.
- Geometry filtering and text-overlap rejection.
- Label association requirement (right-side/nearby token linkage).
- Deduplication and confidence scoring.
- Region output includes:
  - `regionId`, `type`, `bbox`, `confidence`, `value`, `labelTokenIds`, `notes`
- Optional region overlay artifact is generated when debug artifacts are enabled.

## 9. Word List Requirements

- Each page includes `pageWords`: unique token-derived words for that page.
- Document-level `documentWords` is the union of page words.
- Uniqueness is normalized by trimmed, whitespace-collapsed, lowercase key while preserving first-seen display form.

## 10. Structured Extraction Requirements

Current behavior is additive:

- Base extraction stage adds `pages[].keyValueCandidates` using heuristic patterns.
- Field recognition stage promotes candidates into `recognition.fields` with normalization/validation metadata.
- Additional structured stage can add more key-value candidates and fields (including region-derived fields).
- Additional fields are merged by `fieldId`; stronger confidence wins.
- On additive-stage failure, raw OCR evidence is preserved and processing continues with warnings.

## 11. JSON Output Requirements

The DLL is responsible for producing JSON that includes both:

- Raw OCR evidence (tokens/lines/blocks/tables/regions/full text).
- Additive structured interpretation (key-value candidates and recognition fields).

Output behavior:

- Always returns JSON string in `OcrResult.Json`.
- Optionally writes `result.json` to run output folder (`SaveJsonToDisk`).
- Uses camelCase JSON serialization and includes null values.

## 12. Metrics and Diagnostics Requirements

Current metrics/diagnostics include:

- Document-level metrics:
  - `metrics.totalMs`, `metrics.documentOcrMs`, `metrics.pagesMs[]`, `metrics.breakdownMs`
- Page-level timings:
  - `renderMs`, `preprocessMs`, `ocrMs`, `layoutMs`, `tableDetectMs`, `postprocessMs`
- Pipeline stage timings in `extensions.pipelineStageTimings[]` with status (`completed`, `failed`, `skipped`) and optional note.
- Diagnostic extensions:
  - preprocessing profiles
  - debug artifact paths
  - noise diagnostics
  - line reconstruction diagnostics
  - token cleanup stats
  - filtered token IDs
  - field extraction diagnostics
  - structured extraction stats
- Warnings/errors behavior:
  - Validation and runtime issues are recorded in `warnings[]` and `errors[]` with code/message/page/details.

## 13. Performance and Stability Requirements

Current non-functional expectations in implementation:

- Stability-first finalization: return contract JSON even when non-fatal stages warn/fail.
- OCR runs once per page (no second OCR pass for structured extraction).
- Additive structured extraction is failure-tolerant (`preserveOnFailure`) to avoid losing base OCR output.
- Conservative noise filtering is optional and bounded by heuristics.
- Existing contract/evidence structures are preserved while enrichment is added.

## 14. Constraints

- Runtime/framework:
  - `Ocr.Core`: `net10.0`
  - WPF harness: `net10.0-windows`
- Platform/dependencies:
  - OpenCV runtime package is Windows-specific (`OpenCvSharp4.runtime.win`).
  - Ghostscript runtime availability is required for PDF rendering.
  - Tesseract language data is required at `AppContext.BaseDirectory/tessdata` for requested languages.
- Hosting assumptions:
  - Filesystem access for input, optional output JSON, and optional debug artifacts.

## 15. Future Extension Areas

The current code suggests likely extension directions, but these are not fully implemented as formal capabilities:

- Stronger document-type/template classification (`recognition.documentType`, anchors/table mappings are currently placeholder-style structures).
- More advanced form-layout/label association for complex forms.
- Enhanced normalization/validation rules for field values.
- Alternative hosting models (service/API wrappers) around the DLL.

## 16. Summary

The OCR DLL currently provides a full local OCR pipeline for PDF/image files, emitting a rich JSON contract with raw evidence plus additive structured interpretation, along with diagnostics, timings, and optional debug artifacts. The WPF harness is an inspection/debug client of that DLL output, not the OCR logic owner.
