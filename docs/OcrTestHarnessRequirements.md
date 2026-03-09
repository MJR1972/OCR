# OCR Test Harness Requirements

## 1. Overview

- The OCR Test Harness (`Ocr.TestHarness.Wpf`) is a Windows WPF MVVM application used to run and inspect OCR results.
- It allows a user to configure OCR options, invoke the OCR DLL, and review output JSON, diagnostics, and debug artifacts.
- The harness depends on the OCR DLL (`Ocr.Core`) as its processing engine; it does not perform OCR itself.

## 2. Scope

Current harness scope includes:

- Selecting input files and optional output folder.
- Collecting run options and invoking the DLL (`IOcrProcessor.ProcessFile`).
- Displaying returned OCR JSON.
- Displaying run logs and parsed diagnostics.
- Showing summary metrics and per-page summaries.
- Previewing debug artifacts and overlays when available.
- Displaying document/page words.
- Displaying detected table summaries and table details.
- Supporting manual validation/inspection of OCR output and structure.

## 3. Architectural Responsibilities

### OCR DLL responsibilities

- OCR/document intelligence and extraction logic.
- OCR pipeline execution and contract generation.
- Detection/extraction outputs (tokens, lines, blocks, tables, regions, fields).
- Diagnostics, warnings/errors, timing metrics, and optional debug artifact generation.

### OCR Test Harness responsibilities

- UI workflow, option entry, and command execution.
- Calling DLL APIs and rendering returned results.
- Parsing and presenting JSON content for validation and diagnostics.
- Visual review support (tabs, tables, previews, logs).

OCR/document intelligence belongs in the DLL. The harness is for viewing, validation, diagnostics, and testing only, and should not implement OCR intelligence itself.

## 4. Supported User Workflow

Current implemented workflow:

1. User browses and selects a supported file path.
2. User optionally configures OCR options (DPI, language, preprocessing flags, output/debug options).
3. User clicks **Run**.
4. Harness builds `OcrOptions` and executes `_ocrProcessor.ProcessFile(...)` on a background task.
5. Harness receives `OcrResult` (`Json`, optional `OutputJsonPath`).
6. Harness updates:
   - Summary/metrics data
   - JSON text viewer
   - Logs and diagnostics
   - Preview artifacts
   - Words view
   - Tables view
7. User inspects tabs to validate OCR quality and extracted structure.

## 5. UI / Functional Requirements

The UI contains these primary functional areas:

- Header and run controls:
  - Read-only selected file path textbox.
  - **Browse** button for input file.
  - **Run** button bound to busy state.
- Options panel (Expander):
  - Numeric/text options and boolean toggles mirroring DLL options.
  - Output folder browse support.
- Output path label:
  - Displays returned JSON output path if provided.

Implemented tabs/views:

- `Summary`
  - Document summary (file name, type, page count).
  - Timing summary (total/document/render/preprocess/ocr/layout).
  - Quality summary (token/confidence/warnings/errors/table/region/word/field counts).
  - Per-page summary grid.
  - Promoted fields grid.
- `JSON`
  - Read-only raw JSON text display.
- `Log`
  - Timestamped log lines for run status and parsed diagnostics.
- `Preview`
  - Page/view selectors, zoom controls, image display, and status messages.
- `Words`
  - Scope selection (`All` or per-page) and word list display.
- `Tables`
  - Table list, selected table metadata, display rows, header cells, and raw cells.

## 6. Preview and Visualization Requirements

Current preview behavior:

- Uses artifact paths from `extensions.debugArtifactPaths` in DLL JSON.
- Supports page selection and view selection for:
  - Original/Rendered
  - Grayscale
  - Preprocessed
  - Overlay (token)
  - Lines Overlay
  - Blocks Overlay
  - Table Overlay
  - Region Overlay
- Displays only existing files; if missing, shows contextual status messages.
- Supports `Zoom In`, `Zoom Out`, and `Fit to Window` based on scroll viewport size.

## 7. JSON Viewer Requirements

- Harness displays the full JSON string returned by the DLL in a read-only text box.
- The viewer is not a schema editor and does not mutate JSON.
- JSON display is intended for inspection/debugging of the current run output.

## 8. Summary and Metrics Requirements

Current summary processing parses DLL JSON and exposes:

- Document-level metadata and timing totals.
- Breakdown timing values (`render`, `preprocess`, `ocr`, `layout`).
- Warning/error counts.
- Token and confidence summaries.
- Table totals, table method list, and highest-confidence table.
- Region totals (checkbox/radio/checked).
- Document unique word count.
- Key-value candidate total and promoted-field count.
- Per-page grid values (timings, token confidence, table/region counts).
- Promoted recognition fields list.

## 9. Log and Diagnostics Requirements

Current logging behavior includes:

- Run lifecycle logs (start, output path, failure message, warning/error summary).
- Page-level timing and detection summaries parsed from JSON.
- Table detection details, including gridline table details when method indicates lines.
- Parsed diagnostics from extensions:
  - pipeline stage timings
  - page noise diagnostics
  - line reconstruction diagnostics
  - token cleanup diagnostics
  - debug artifact paths
- Additional warning-derived messages (e.g., region stats, token cleanup stats when present).

## 10. Words View Requirements

Current words behavior:

- Data source is DLL-provided `documentWords` and `pages[].pageWords`.
- Scope options include `All` and each page (`Page N`) found in JSON.
- Word lists are displayed sorted alphabetically (case-insensitive).
- No text search/filter beyond scope selection is currently implemented.

## 11. Tables View Requirements

Current table inspection behavior:

- Table list shows page, table id, detection method, confidence, rows, cols.
- Selecting a table shows:
  - table metadata (method, explicit gridlines flag, confidence, bbox, token coverage)
  - associated table overlay path (if present)
  - header columns
  - raw header cells
  - raw cells
  - extracted display rows
- Display rows are built from `rows[].values` when available; otherwise assembled from raw cells and header columns.
- All table information is consumed from DLL JSON.

## 12. Data Source Requirements

The harness must consume and display DLL-produced data.

- OCR results come from `_ocrProcessor.ProcessFile(...)`.
- The harness must not generate OCR evidence or OCR extraction output itself.
- The harness must not implement business/OCR extraction logic.
- The harness is a visualization/inspection layer over DLL outputs.

## 13. Error Handling Requirements

Current error handling behavior:

- If run invocation throws, harness logs failure and shows a message box.
- If returned JSON is empty/invalid for a specific parser path, harness falls back gracefully (reset/empty states and log/status messages).
- Missing preview artifacts produce preview status messages rather than crashes.
- Empty/no tables/words/fields are represented as empty lists with informative status messages.
- DLL warnings/errors are surfaced as counts and through parsed log messages where applicable.

## 14. Performance and Stability Requirements

Current non-functional expectations:

- Harness remains a thin viewer/debugger over DLL output.
- OCR processing runs on a background task to keep UI responsive.
- Busy-state prevents duplicate run execution from UI command availability.
- Diagnostics are additive display behavior; they do not alter OCR output.
- Large JSON/documents are displayed in practical read-only form without additional transformation pipelines.

## 15. Constraints

- Platform: Windows only (`net10.0-windows`, WPF).
- Architecture: MVVM with `MainViewModel` as primary orchestration layer.
- Dependency: direct dependence on `Ocr.Core` DLL for OCR execution.
- Intended use: internal validation/testing/inspection harness, not production OCR service host.

## 16. Future Extension Areas

Potential harness extensions (not fully implemented today):

- Dedicated field review/edit workflow with confidence triage.
- Side-by-side run comparison for regression analysis.
- Enhanced validation tooling (rule checks, issue drill-downs, diff views).
- Template/workflow tooling for repeatable document QA runs.
- Broader diagnostics dashboards and export/reporting utilities.

## 17. Summary

The OCR Test Harness currently provides a Windows WPF UI that runs the OCR DLL, displays returned JSON and diagnostics, and supports structured manual review across summary, logs, preview overlays, words, and tables. It is a consumer and validator of DLL output, not an OCR intelligence implementation.
