# OCR System Test Cases

## 1. Overview

- This document defines manual test cases for the current OCR system implementation.
- Scope:
  - OCR DLL behavior exercised through the harness (`Ocr.Core`).
  - WPF OCR Test Harness behavior (`Ocr.TestHarness.Wpf`) as viewer/debug UI.
- The harness is used to invoke and validate DLL output.
- DLL output is validated through JSON, summary, logs, words, tables, and preview overlays.

## 2. Test Execution Instructions

- Execute tests by section or end-to-end.
- For each test, record:
  - `Actual Result`
  - `Status`
  - `Notes`
- Allowed `Status` values:
  - `Pass`
  - `Fail`
  - `Blocked`
  - `Not Run`
- Capture evidence where relevant:
  - screenshots
  - copied log lines
  - JSON snippets
  - artifact file paths

## 3. Test Case Template Format

Use this format for each test case:

- `Test Case ID`
- `Title`
- `Area`
- `Objective`
- `Preconditions`
- `Test Data / Input`
- `Steps`
- `Expected Result`
- `Actual Result`
- `Status`
- `Notes`

---

## A. Application Launch / Basic Harness Functionality

### TC-A01
- Test Case ID: `TC-A01`
- Title: Launch harness
- Area: Startup
- Objective: Verify application starts and UI loads.
- Preconditions: Build succeeds.
- Test Data / Input: None.
- Steps:
  1. Start `Ocr.TestHarness.Wpf`.
- Expected Result:
  - Main window opens with no crash.
  - Tabs visible: `Summary`, `JSON`, `Log`, `Preview`, `Words`, `Tables`.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-A02
- Test Case ID: `TC-A02`
- Title: Default run state
- Area: Run UX
- Objective: Verify behavior with no selected file.
- Preconditions: Fresh app open.
- Test Data / Input: None.
- Steps:
  1. Confirm file path is empty.
  2. Try to run.
- Expected Result:
  - Run command is not executable until file is selected.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-A03
- Test Case ID: `TC-A03`
- Title: File browse/select workflow
- Area: Input UX
- Objective: Verify browse updates selected file.
- Preconditions: App open.
- Test Data / Input: Supported file (`.pdf`, `.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`, `.gif`, `.bmp`).
- Steps:
  1. Click file `Browse`.
  2. Select file.
- Expected Result:
  - Path field updates.
  - Log contains `Selected file: ...`.
  - Run becomes executable.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-A04
- Test Case ID: `TC-A04`
- Title: Run lifecycle behavior
- Area: Run UX
- Objective: Verify busy state and completion behavior.
- Preconditions: Valid file selected.
- Test Data / Input: Any valid sample.
- Steps:
  1. Click `Run`.
  2. Observe button text during run.
  3. Observe completion.
- Expected Result:
  - Button text changes to `Running...` during run, then back to `Run`.
  - Log starts with `Starting OCR processor.`
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-A05
- Test Case ID: `TC-A05`
- Title: Invalid/unsupported input handling from UI
- Area: Negative
- Objective: Verify harness stability on unsupported file type.
- Preconditions: Unsupported file available.
- Test Data / Input: `.txt` file.
- Steps:
  1. Select unsupported file.
  2. Run OCR.
- Expected Result:
  - JSON still displayed.
  - `errors[]` includes unsupported-extension error.
  - Harness remains stable.
- Actual Result:
- Status: `Not Run`
- Notes:

## B. File Input / Document Loading

### TC-B01
- Test Case ID: `TC-B01`
- Title: Single-page image input
- Area: DLL Input
- Objective: Verify image load path.
- Preconditions: Valid image sample.
- Test Data / Input: `png` or `jpg` with text.
- Steps:
  1. Run OCR.
  2. Inspect JSON source/page fields.
- Expected Result:
  - `document.source.fileType = image`.
  - `pages` contains one page.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-B02
- Test Case ID: `TC-B02`
- Title: PDF input and page rendering
- Area: DLL Input
- Objective: Verify PDF render path.
- Preconditions: Valid multi-page PDF.
- Test Data / Input: 2+ page PDF.
- Steps:
  1. Run OCR.
  2. Inspect Summary and JSON.
- Expected Result:
  - `document.source.fileType = pdf`.
  - `document.source.pageCount` reflects rendered pages.
  - Render timing values/log lines present.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-B03
- Test Case ID: `TC-B03`
- Title: Missing file behavior
- Area: DLL Input Negative
- Objective: Verify missing file error.
- Preconditions: Select file, then remove it externally.
- Test Data / Input: Deleted/moved selected file.
- Steps:
  1. Click `Run`.
- Expected Result:
  - JSON includes `file_not_found`.
  - No crash.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-B04
- Test Case ID: `TC-B04`
- Title: Corrupt file behavior
- Area: DLL Input Negative
- Objective: Verify graceful failure for decode/render issues.
- Preconditions: Corrupt file available.
- Test Data / Input: Corrupt PDF or image.
- Steps:
  1. Run OCR.
  2. Review JSON errors/logs.
- Expected Result:
  - Error(s) recorded in JSON.
  - Harness remains responsive.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-B05
- Test Case ID: `TC-B05`
- Title: Multi-page handling in harness
- Area: Harness + DLL
- Objective: Verify multi-page outputs are surfaced.
- Preconditions: Multi-page PDF.
- Test Data / Input: 3+ page PDF.
- Steps:
  1. Run OCR.
  2. Check per-page summary and preview page selector.
- Expected Result:
  - One summary row per page.
  - Preview page dropdown contains page indices.
- Actual Result:
- Status: `Not Run`
- Notes:

## C. OCR DLL Core Processing

### TC-C01
- Test Case ID: `TC-C01`
- Title: OCR request/response path
- Area: DLL Core
- Objective: Verify harness receives `OcrResult` and displays JSON.
- Preconditions: Valid file selected.
- Test Data / Input: Any valid sample.
- Steps:
  1. Run OCR.
  2. Open JSON tab.
- Expected Result:
  - JSON string present.
  - Output path label updates (or shows not written when applicable).
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-C02
- Test Case ID: `TC-C02`
- Title: Core JSON sections
- Area: DLL Core
- Objective: Verify contract baseline sections.
- Preconditions: Successful run.
- Test Data / Input: Any valid sample.
- Steps:
  1. Inspect JSON top-level keys.
- Expected Result:
  - Includes `schema`, `document`, `metrics`, `pages`, `recognition`, `warnings`, `errors`, `documentWords`, `extensions`.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-C03
- Test Case ID: `TC-C03`
- Title: Raw OCR evidence population
- Area: DLL Core
- Objective: Verify page evidence structures.
- Preconditions: Non-blank document.
- Test Data / Input: Text document.
- Steps:
  1. Inspect `pages[0]`.
- Expected Result:
  - `tokens`, `lines`, `blocks`, `text.fullText` populated or explicitly empty for blank content.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-C04
- Test Case ID: `TC-C04`
- Title: Token geometry and confidence
- Area: DLL Core
- Objective: Verify token bbox/confidence fields.
- Preconditions: At least one token exists.
- Test Data / Input: Readable text sample.
- Steps:
  1. Inspect token object.
- Expected Result:
  - Token has `bbox` and confidence fields.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-C05
- Test Case ID: `TC-C05`
- Title: Warning/error structures
- Area: DLL Core
- Objective: Verify issue entry structure.
- Preconditions: Use input likely to produce warnings/errors.
- Test Data / Input: Low-quality or invalid sample.
- Steps:
  1. Inspect `warnings[]` and `errors[]` entries.
- Expected Result:
  - Entries include code/severity/message and optional page/details.
- Actual Result:
- Status: `Not Run`
- Notes:

## D. Preprocessing / Rendering / OCR Metrics

### TC-D01
- Test Case ID: `TC-D01`
- Title: Document/page timing population
- Area: Metrics
- Objective: Verify metric fields are present and non-negative.
- Preconditions: Successful run.
- Test Data / Input: Any valid sample.
- Steps:
  1. Inspect `metrics` and `pages[].timing`.
- Expected Result:
  - `totalMs`, `documentOcrMs`, `pagesMs`, and per-page timing fields exist and are >= 0.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-D02
- Test Case ID: `TC-D02`
- Title: Breakdown metric population
- Area: Metrics
- Objective: Verify breakdown timing sections.
- Preconditions: Successful run.
- Test Data / Input: PDF preferred.
- Steps:
  1. Inspect `metrics.breakdownMs`.
- Expected Result:
  - `renderMs`, `preprocessMs`, `ocrMs`, `layoutMs`, `tableDetectMs`, `recognitionMs`, `postprocessMs` populated.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-D03
- Test Case ID: `TC-D03`
- Title: Stage timing visibility in log
- Area: Harness Diagnostics
- Objective: Verify pipeline stage timings are visible.
- Preconditions: Successful run.
- Test Data / Input: Any valid sample.
- Steps:
  1. Open `Log` tab and locate stage timing section.
- Expected Result:
  - Logs show stage, duration, status, and optional note.
- Actual Result:
- Status: `Not Run`
- Notes:
## E. Word List Functionality

### TC-E01
- Test Case ID: `TC-E01`
- Title: Document words population
- Area: Words
- Objective: Verify `documentWords` is displayed.
- Preconditions: Run document with text.
- Test Data / Input: Text-rich sample.
- Steps:
  1. Run OCR.
  2. Open JSON and Words tab.
- Expected Result:
  - JSON `documentWords` populated.
  - Words tab (scope `All`) shows same set/count.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-E02
- Test Case ID: `TC-E02`
- Title: Page words population and scope switching
- Area: Words
- Objective: Verify per-page words and dropdown behavior.
- Preconditions: Multi-page run.
- Test Data / Input: 2+ page text sample.
- Steps:
  1. Open `Words` tab.
  2. Switch scope from `All` to `Page 1`, `Page 2`.
- Expected Result:
  - Scope list includes available pages.
  - Displayed words change by scope.
  - `Word Count` updates accordingly.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-E03
- Test Case ID: `TC-E03`
- Title: Word ordering and uniqueness display
- Area: Words
- Objective: Verify displayed ordering and uniqueness expectations.
- Preconditions: Successful run with words.
- Test Data / Input: Any text sample.
- Steps:
  1. Inspect word list ordering.
- Expected Result:
  - Displayed words are sorted alphabetically (case-insensitive).
  - No obvious duplicate display entries in a scope.
- Actual Result:
- Status: `Not Run`
- Notes:

## F. Tables Functionality

### TC-F01
- Test Case ID: `TC-F01`
- Title: No-table handling
- Area: Tables
- Objective: Verify empty table case in UI and JSON.
- Preconditions: Run non-table document.
- Test Data / Input: Plain text sample.
- Steps:
  1. Open `Tables` tab.
  2. Inspect JSON `pages[].tables`.
- Expected Result:
  - No table rows listed.
  - Status message indicates no tables detected.
  - JSON table arrays empty or omitted per page as produced.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-F02
- Test Case ID: `TC-F02`
- Title: Table list and selection
- Area: Tables
- Objective: Verify detected tables appear and selection works.
- Preconditions: Table document available.
- Test Data / Input: Document containing table(s).
- Steps:
  1. Run OCR.
  2. Open `Tables` tab.
  3. Select table row(s).
- Expected Result:
  - List shows page/table id/method/confidence/rows/cols.
  - Selection updates details panel.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-F03
- Test Case ID: `TC-F03`
- Title: Table details and cell data
- Area: Tables
- Objective: Verify header/cell/value inspection views.
- Preconditions: Table selected.
- Test Data / Input: Table document.
- Steps:
  1. Inspect Header Columns, Raw Header Cells, Raw Cells, Extracted Table Rows grids.
- Expected Result:
  - Grid data populates from selected table JSON.
  - Extracted rows display `rows[].values` when available, with fallback from raw cells.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-F04
- Test Case ID: `TC-F04`
- Title: Table confidence and overlay path
- Area: Tables + Preview
- Objective: Verify confidence/coverage metadata and overlay references.
- Preconditions: Debug artifacts enabled; table detected.
- Test Data / Input: Table sample.
- Steps:
  1. Enable `Save Debug Artifacts`.
  2. Run OCR.
  3. Select table and inspect confidence/coverage/overlay path.
  4. Preview -> `Table Overlay`.
- Expected Result:
  - Metadata fields populate.
  - Overlay path shown when artifact exists.
  - Preview shows table overlay when file exists.
- Actual Result:
- Status: `Not Run`
- Notes:

## G. Region Detection Functionality

### TC-G01
- Test Case ID: `TC-G01`
- Title: Region-empty scenario
- Area: Regions
- Objective: Verify behavior when no checkbox/radio regions exist.
- Preconditions: Non-form sample.
- Test Data / Input: Plain text document.
- Steps:
  1. Run OCR.
  2. Inspect Summary region counts and JSON `pages[].regions`.
- Expected Result:
  - Region counts are zero.
  - Region arrays empty or no checkbox/radio entries.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-G02
- Test Case ID: `TC-G02`
- Title: Region population scenario
- Area: Regions
- Objective: Verify checkbox/radio detections are exposed.
- Preconditions: Form with checkboxes/radio available.
- Test Data / Input: Region-rich form.
- Steps:
  1. Run OCR.
  2. Inspect JSON region entries.
- Expected Result:
  - Region entries include `type`, `bbox`, `confidence` and `value` where detected.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-G03
- Test Case ID: `TC-G03`
- Title: Region label association visibility
- Area: Regions
- Objective: Verify `labelTokenIds` behavior.
- Preconditions: Labeled region sample.
- Test Data / Input: Checkbox/radio form with nearby labels.
- Steps:
  1. Inspect region entries.
- Expected Result:
  - `labelTokenIds` populated where association is found.
  - Can be empty for ambiguous items.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-G04
- Test Case ID: `TC-G04`
- Title: Region overlay preview
- Area: Regions + Preview
- Objective: Verify region overlays can be viewed.
- Preconditions: Debug artifacts enabled; regions detected.
- Test Data / Input: Region sample.
- Steps:
  1. Run OCR with debug artifacts.
  2. Preview -> select `Region Overlay`.
- Expected Result:
  - Region overlay appears when generated.
  - Missing overlay yields status message without crash.
- Actual Result:
- Status: `Not Run`
- Notes:

## H. Structured Extraction Functionality

### TC-H01
- Test Case ID: `TC-H01`
- Title: Key-value candidate population
- Area: Structured Extraction
- Objective: Verify `keyValueCandidates` availability.
- Preconditions: Structured text sample.
- Test Data / Input: Document containing label/value patterns.
- Steps:
  1. Run OCR.
  2. Inspect `pages[].keyValueCandidates`.
- Expected Result:
  - Candidates populated where applicable.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-H02
- Test Case ID: `TC-H02`
- Title: Recognition fields population
- Area: Structured Extraction
- Objective: Verify `recognition.fields` and summary field list.
- Preconditions: Same as H01.
- Test Data / Input: Structured document.
- Steps:
  1. Inspect JSON `recognition.fields`.
  2. Open Summary promoted fields grid.
- Expected Result:
  - Fields present when promoted.
  - Summary count aligns with JSON count.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-H03
- Test Case ID: `TC-H03`
- Title: Additive-only structured behavior
- Area: Structured Extraction Guardrail
- Objective: Verify raw OCR evidence remains intact.
- Preconditions: Run with populated structured output.
- Test Data / Input: Document with both raw text and fields.
- Steps:
  1. Confirm raw evidence arrays exist.
  2. Confirm structured sections exist.
- Expected Result:
  - Structured extraction does not replace/remove raw evidence.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-H04
- Test Case ID: `TC-H04`
- Title: Field source/validation/review metadata
- Area: Structured Extraction
- Objective: Verify field metadata structures are present.
- Preconditions: At least one recognized field.
- Test Data / Input: Structured form.
- Steps:
  1. Inspect a field object.
- Expected Result:
  - Field includes source (`pageIndex`, `bbox`, `tokenIds`, method), confidence, validation/review objects.
- Actual Result:
- Status: `Not Run`
- Notes:

## I. JSON Viewer / Result Display

### TC-I01
- Test Case ID: `TC-I01`
- Title: JSON tab rendering
- Area: JSON Viewer
- Objective: Verify result JSON is displayed and read-only.
- Preconditions: Completed run.
- Test Data / Input: Any valid sample.
- Steps:
  1. Open JSON tab.
- Expected Result:
  - JSON text visible, scrollable, and not editable.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-I02
- Test Case ID: `TC-I02`
- Title: JSON refresh after rerun
- Area: JSON Viewer
- Objective: Verify JSON updates to latest run output.
- Preconditions: Two distinct samples.
- Test Data / Input: File A then file B.
- Steps:
  1. Run file A.
  2. Run file B.
  3. Compare key values (file name/page count).
- Expected Result:
  - JSON reflects latest run, not stale prior output.
- Actual Result:
- Status: `Not Run`
- Notes:
## J. Preview / Image Display / Overlay Display

### TC-J01
- Test Case ID: `TC-J01`
- Title: Preview page selector and mode selector
- Area: Preview
- Objective: Verify selectors populate and switch state.
- Preconditions: Debug artifacts available.
- Test Data / Input: Multi-page run with artifacts.
- Steps:
  1. Open Preview tab.
  2. Change page and view selections.
- Expected Result:
  - Page selector contains available pages.
  - View selector supports implemented modes.
  - Preview updates or shows clear missing-artifact message.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-J02
- Test Case ID: `TC-J02`
- Title: Base image views
- Area: Preview
- Objective: Verify Original/Rendered, Grayscale, Preprocessed views.
- Preconditions: Debug artifacts generated.
- Test Data / Input: Any valid sample.
- Steps:
  1. Switch among base image modes.
- Expected Result:
  - Corresponding images display when files exist.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-J03
- Test Case ID: `TC-J03`
- Title: Overlay image views
- Area: Preview
- Objective: Verify overlay display modes.
- Preconditions: `Save Token Overlay` and/or debug artifacts enabled.
- Test Data / Input: Document likely to produce overlays.
- Steps:
  1. Select `Overlay`, `Lines Overlay`, `Blocks Overlay`, `Table Overlay`, `Region Overlay`.
- Expected Result:
  - Available overlays display correctly.
  - Missing overlays show status message and do not crash app.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-J04
- Test Case ID: `TC-J04`
- Title: Zoom controls
- Area: Preview
- Objective: Verify `Zoom In`, `Zoom Out`, `Fit to Window`.
- Preconditions: Preview image visible.
- Test Data / Input: Any artifact image.
- Steps:
  1. Use zoom buttons.
  2. Use fit button.
- Expected Result:
  - Zoom changes as expected.
  - Fit sets practical scaling based on viewport.
- Actual Result:
- Status: `Not Run`
- Notes:

## K. Summary Tab / Log Tab / Diagnostics

### TC-K01
- Test Case ID: `TC-K01`
- Title: Summary tab data population
- Area: Summary
- Objective: Verify summary sections and values.
- Preconditions: Successful run.
- Test Data / Input: Any valid sample.
- Steps:
  1. Open Summary tab.
- Expected Result:
  - Document summary fields populated.
  - Timing summary populated.
  - Quality summary populated.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-K02
- Test Case ID: `TC-K02`
- Title: Per-page summary grid
- Area: Summary
- Objective: Verify page-level metrics/counts.
- Preconditions: Multi-page or single-page successful run.
- Test Data / Input: PDF preferred.
- Steps:
  1. Inspect page grid columns.
- Expected Result:
  - Rows include page index, token count, confidence, timings, table/region counts.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-K03
- Test Case ID: `TC-K03`
- Title: Promoted fields grid
- Area: Summary
- Objective: Verify field summary display.
- Preconditions: Run document with recognizable fields.
- Test Data / Input: Structured form sample.
- Steps:
  1. Inspect promoted fields grid.
- Expected Result:
  - Field rows display id, label, value, confidence when available.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-K04
- Test Case ID: `TC-K04`
- Title: Log tab run + diagnostics visibility
- Area: Log
- Objective: Verify logs include run status and diagnostics.
- Preconditions: Completed run.
- Test Data / Input: Any valid sample.
- Steps:
  1. Open Log tab and review lines.
- Expected Result:
  - Includes run start/output path/timing summaries.
  - Includes warnings/errors summary.
  - Includes available diagnostics (pipeline timings/noise/reconstruction/token cleanup/artifacts).
- Actual Result:
- Status: `Not Run`
- Notes:

## L. Error Handling / Negative Tests

### TC-L01
- Test Case ID: `TC-L01`
- Title: Invalid/missing path
- Area: Negative
- Objective: Verify path error handling.
- Preconditions: Missing/deleted file selected.
- Test Data / Input: Invalid existing selection.
- Steps:
  1. Run OCR.
- Expected Result:
  - JSON contains path-related error.
  - App remains stable.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-L02
- Test Case ID: `TC-L02`
- Title: Unsupported extension
- Area: Negative
- Objective: Verify unsupported extension handling.
- Preconditions: Unsupported file available.
- Test Data / Input: `.txt`.
- Steps:
  1. Run OCR.
- Expected Result:
  - Error in JSON.
  - UI continues to function.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-L03
- Test Case ID: `TC-L03`
- Title: Partial failure tolerance
- Area: Negative / Stability
- Objective: Verify output is preserved where processing is partially successful.
- Preconditions: Input likely to produce partial render/extraction issues.
- Test Data / Input: Imperfect/corrupt multi-page sample.
- Steps:
  1. Run OCR.
  2. Inspect pages/errors.
- Expected Result:
  - Processing reports errors/warnings for failed portions.
  - Available output still present.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-L04
- Test Case ID: `TC-L04`
- Title: Additive stage failure preservation (conditional)
- Area: Negative / Architecture
- Objective: Verify raw OCR evidence is preserved if additive structured stage fails.
- Preconditions: Ability to induce additive-stage failure (may not be reproducible with normal data).
- Test Data / Input: Complex edge sample.
- Steps:
  1. Run OCR.
  2. Check for structured-stage warning.
  3. Verify raw evidence still present.
- Expected Result:
  - On additive-stage failure, warning appears and raw evidence remains.
  - Mark `Blocked` if this condition cannot be safely induced.
- Actual Result:
- Status: `Not Run`
- Notes:

## M. Regression Safety / Architecture Guardrail Tests

### TC-M01
- Test Case ID: `TC-M01`
- Title: Raw evidence contract guardrail
- Area: Guardrail
- Objective: Verify raw OCR evidence layer is retained.
- Preconditions: Successful run.
- Test Data / Input: Any non-blank sample.
- Steps:
  1. Verify tokens/lines/blocks/fullText exist.
- Expected Result:
  - Raw evidence structures remain present.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-M02
- Test Case ID: `TC-M02`
- Title: Structured output additive guardrail
- Area: Guardrail
- Objective: Verify structured output does not replace raw evidence.
- Preconditions: Run with fields/candidates.
- Test Data / Input: Structured sample.
- Steps:
  1. Verify both raw and structured sections.
- Expected Result:
  - Raw and structured sections coexist.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-M03
- Test Case ID: `TC-M03`
- Title: Harness viewer-role guardrail
- Area: Guardrail
- Objective: Validate harness remains consumer/visualizer.
- Preconditions: Completed run and JSON inspection.
- Test Data / Input: Any valid sample.
- Steps:
  1. Confirm displayed values are read from returned JSON structures.
- Expected Result:
  - No independent OCR extraction performed in UI workflow.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-M04
- Test Case ID: `TC-M04`
- Title: Contract stability baseline
- Area: Guardrail
- Objective: Verify core sections remain stable across runs.
- Preconditions: Two different runs.
- Test Data / Input: Image and PDF.
- Steps:
  1. Compare top-level section presence across runs.
- Expected Result:
  - Core contract sections remain present and compatible.
- Actual Result:
- Status: `Not Run`
- Notes:

## N. Performance / Stability Sanity Tests

### TC-N01
- Test Case ID: `TC-N01`
- Title: Small image timing sanity
- Area: Performance
- Objective: Verify practical completion and non-negative timings.
- Preconditions: Small image sample.
- Test Data / Input: Single-page text image.
- Steps:
  1. Run OCR.
  2. Inspect summary timings.
- Expected Result:
  - Run completes and timings are populated.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-N02
- Test Case ID: `TC-N02`
- Title: Multi-page PDF timing sanity
- Area: Performance
- Objective: Verify practical completion for larger input.
- Preconditions: Multi-page PDF sample.
- Test Data / Input: 5+ page PDF.
- Steps:
  1. Run OCR.
- Expected Result:
  - Run completes without crash/hang.
  - Results update across tabs.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-N03
- Test Case ID: `TC-N03`
- Title: Repeated run stability
- Area: Stability
- Objective: Verify multiple runs remain stable.
- Preconditions: Two or more sample files.
- Test Data / Input: Alternate files across runs.
- Steps:
  1. Execute 5-10 runs.
- Expected Result:
  - No crash.
  - JSON/summary/log/preview/words/tables refresh each run.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-N04
- Test Case ID: `TC-N04`
- Title: No duplicate run trigger per click
- Area: Stability
- Objective: Verify single-click single-run behavior.
- Preconditions: Valid file selected.
- Test Data / Input: Any valid sample.
- Steps:
  1. Click Run once.
  2. Inspect logs for single run lifecycle.
- Expected Result:
  - No obvious duplicate execution for a single click.
- Actual Result:
- Status: `Not Run`
- Notes:

## O. UI Control Coverage

### TC-O01
- Test Case ID: `TC-O01`
- Title: File browse button
- Area: UI Controls
- Objective: Verify file chooser control.
- Preconditions: App open.
- Test Data / Input: Supported file.
- Steps:
  1. Click file `Browse` and choose file.
- Expected Result:
  - Dialog opens and selection updates path.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-O02
- Test Case ID: `TC-O02`
- Title: Output folder browse button
- Area: UI Controls
- Objective: Verify output folder selection.
- Preconditions: App open.
- Test Data / Input: Existing folder.
- Steps:
  1. Click options `Browse` for output folder.
  2. Select folder.
- Expected Result:
  - Output folder textbox updates.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-O03
- Test Case ID: `TC-O03`
- Title: Options controls coverage
- Area: UI Controls
- Objective: Verify visible options can be edited/toggled and used in run.
- Preconditions: App open.
- Test Data / Input: Change these controls:
  - Text/numeric: `TargetDpi`, `Language`, `PageSegMode`, `EngineMode`, `MaxDeskewDegrees`, `OutputFolder`, `ProfileName`
  - Checkboxes: `EnableDeskew`, `PreserveInterwordSpaces`, `EnableDenoise`, `EnableBinarization`, `EnableContrastEnhancement`, `SaveDebugArtifacts`, `SaveTokenOverlay`, `EnableNoiseFiltering`
- Steps:
  1. Change control values.
  2. Run OCR.
- Expected Result:
  - UI accepts values/toggles.
  - Run executes; invalid combinations produce warnings/errors rather than crashes.
- Actual Result:
- Status: `Not Run`
- Notes:

### TC-O04
- Test Case ID: `TC-O04`
- Title: Tabs and selectors coverage
- Area: UI Controls
- Objective: Verify major interactive controls work.
- Preconditions: Completed run with enough data.
- Test Data / Input: Multi-page sample with table/words if possible.
- Steps:
  1. Navigate all tabs.
  2. Use preview page/view selectors.
  3. Use words scope dropdown.
  4. Use table selection grid.
- Expected Result:
  - Controls respond and update bound views.
- Actual Result:
- Status: `Not Run`
- Notes:

---

## 5. Test Data Recommendations

Recommended manual test documents:

- Simple typed image (clear text).
- Simple one-page PDF.
- Multi-page PDF.
- Document with table(s).
- Document with checkbox/radio controls.
- Document with label/value style fields.
- Poor-quality/noisy scan.
- Blank or near-blank page.
- Unsupported extension sample (for example `.txt`).
- Corrupt image/PDF sample.

Notes:

- No automatic data generation is required by this test plan.
- Use representative internal samples already available to the team.

---

## 6. Pass/Fail Friendly Tracking

For each test case, fill in:

- `Actual Result`
- `Status` (`Pass`, `Fail`, `Blocked`, `Not Run`)
- `Notes`

Include evidence references where useful:

- screenshot names
- log excerpts
- JSON snippets
- artifact paths

---

## 7. Accuracy Notes

- These test cases are based on currently implemented OCR DLL and harness behavior.
- Some outcomes are content-dependent (table/region/field detections may vary by sample quality and layout).
- For content-dependent tests, the core pass criterion is:
  - stable execution
  - expected structure presence
  - diagnostics clarity
  - no harness crash
