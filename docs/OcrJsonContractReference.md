# OCR JSON Contract Reference

## 1. Overview

- The OCR JSON contract is the primary output of `OcrProcessor.ProcessFile(...)`.
- It contains:
  - raw OCR evidence (tokens/lines/blocks/tables/regions/full text)
  - additive structured interpretation (key-value candidates and recognition fields)
  - metadata, metrics, warnings/errors, and diagnostics.
- Raw OCR evidence is the source of truth.
- Structured extraction is additive only and should not be treated as a replacement for raw evidence.

## 2. Contract Design Principles

- Raw evidence is preserved for traceability and downstream re-interpretation.
- Structured sections are additive and confidence-driven.
- Contract supports both debugging and production-style consumption.
- Bounding boxes (`bbox`) and confidence fields are first-class evidence.
- Warnings/errors/metrics/diagnostics are intentionally part of the same payload.

## 3. Top-Level JSON Structure

Top-level shape (camelCase JSON):

- `schema` (metadata)
- `definitions` (reference/interpretation metadata)
- `document` (document + processing metadata)
- `metrics` (timings)
- `pages` (raw evidence + page-level structures)
- `recognition` (additive interpretation)
- `review` (review metadata)
- `warnings` (issues, including `info`/`warning` severities)
- `errors` (error issues)
- `documentWords` (unique document-level words)
- `extensions` (diagnostics/implementation details)

Classification guidance:

- Evidence: `pages.tokens/lines/blocks/tables/regions/text`.
- Interpretation: `pages.keyValueCandidates`, `recognition.fields`.
- Metadata: `schema`, `definitions`, `document`.
- Diagnostics: `metrics`, `warnings/errors`, `extensions`.

## 4. Schema Section Reference

- `schema.name` (`string`): logical contract name.
- `schema.version` (`string`): contract version string.
- `schema.status` (`string`): schema maturity/status label.

Use:

- Versioning and compatibility checks.

## 5. Definitions Section Reference

### `definitions.coordinateSystem`

- `unit` (`string`), `origin` (`string`), `xDirection` (`string`), `yDirection` (`string`)
- `bboxFormat` (`string`, current `x,y,w,h`)
- `pageRotationAppliedToCoords` (`bool`)
- `notes` (`string`)

### `definitions.confidence`

- `scale` (`string`, current `0to1`)
- threshold values (`double`):
  - `lowTokenThreshold`
  - `lowLineThreshold`
  - `lowBlockThreshold`
  - `lowCellThreshold`
  - `lowTableThreshold`
  - `lowFieldThreshold`

### `definitions.readingOrder`

- `default` (`string`)
- `supported` (`string[]`)

### `definitions.enums`

Arrays of supported enum-style values (`string[]`):

- `severity`, `tokenType`, `blockType`, `regionType`, `normalizedType`, `tableDetectionMethod`, `fieldSourceMethod`

Consumer note:

- Use definitions as interpretation aids, not strict runtime guarantees for every field value.

## 6. Document Section Reference

### `document`

- `documentId` (`string`)
- `correlationId` (`string`)
- `createdUtc` (`string`, ISO date-time)
- `source` (`object`)
- `processing` (`object`)

### `document.source`

- `originalFileName` (`string`)
- `fileType` (`string`, typically `pdf` or `image`)
- `fileHashSha256` (`string?`, nullable)
- `pageCount` (`int`)
- `mimeType` (`string`)

### `document.processing`

- `pipelineVersion` (`string`)
- `engine` (`object`)
- `render` (`object`)
- `preprocessing` (`object`)
- `postprocessing` (`object`)
- `confidencePolicy` (`object`)

#### `engine`

- `name` (`string`), `version` (`string`)
- `language` (`string[]`)
- `params.psm` (`string`), `params.oem` (`string`)

#### `render`

- `dpiOriginal` (`int`)
- `dpiNormalizedTo` (`int`)
- `colorMode` (`string`)
- `pageImageFormat` (`string`)

#### `preprocessing`

- `rotation` (`attempted/applied/degrees/confidence`)
- `deskew` (`attempted/applied/degrees/confidence`)
- `denoise`, `binarization`, `contrastEnhancement` (`enabled`, `method`)

#### `postprocessing`

- `fixCommonOcrErrors` (`bool`)
- `normalizeWhitespace` (`bool`)

#### `confidencePolicy`

- `scale` (`string`)
- `lowTokenThreshold` (`double`)
- `aggregation` formulas (`string` fields for line/block/cell/row/table/field confidence semantics)

## 7. Metrics Section Reference

### `metrics`

- `totalMs` (`int`)
- `documentOcrMs` (`int`)
- `pagesMs` (`int[]`)
- `breakdownMs` (`object`)

### `metrics.breakdownMs`

- `renderMs`, `preprocessMs`, `ocrMs`, `layoutMs`, `tableDetectMs`, `recognitionMs`, `postprocessMs` (`int`)

Relationship to extensions:

- Detailed stage-by-stage timings are exposed at `extensions.pipelineStageTimings[]`.

## 8. Pages Section Reference

`pages` is an array of page objects.

### `pages[]` common fields

- `pageIndex` (`int`)
- `pageId` (`string`)
- `size` (`object`)
- `timing` (`object`)
- `quality` (`object`)
- `text` (`object`)
- `tokens` (`TokenInfo[]`)
- `lines` (`LineInfo[]`)
- `blocks` (`BlockInfo[]`)
- `tables` (`TableInfo[]`)
- `keyValueCandidates` (`KeyValueCandidateInfo[]`)
- `regions` (`RegionInfo[]`)
- `pageWords` (`string[]`)
- `unassignedTokenIds` (`string[]`)
- `artifacts` (`object`)

### `size`

- `widthPx`, `heightPx`, `dpi` (`int`)
- `rotationDegrees` (`double`)

### `timing`

- `renderMs`, `preprocessMs`, `ocrMs`, `layoutMs`, `tableDetectMs`, `postprocessMs` (`int`)

### `quality`

- `meanTokenConfidence` (`double`)
- `lowConfidenceTokenCount` (`int`)
- `blankPage` (`bool`)

### `text`

- `fullText` (`string`)
- `readingOrder` (`string`)

### `artifacts`

- `pageImageRef` (`string?`)
- `debugOverlayRef` (`string?`)

## 9. Tokens Reference

Tokens are raw OCR evidence.

### `pages[].tokens[]`

- `id` (`string`)
- `type` (`string`)
- `text` (`string`)
- `confidence` (`double`, typically 0..1)
- `confidenceRaw` (`double?`)
- `isLowConfidence` (`bool`)
- `bbox` (`{ x:int, y:int, w:int, h:int }`)
- `blockId` (`string`)
- `lineId` (`string`)
- `alternates` (`[{ text:string, confidence:double }]`)
- `source` (`{ engine:string, level:string }`)

Usage guidance:

- Use `bbox` for geometry/visual traceability.
- Use `confidence` and `isLowConfidence` for review heuristics.
- Use `id` with `tokenIds` in other sections for provenance.

## 10. Lines Reference

### `pages[].lines[]`

- `lineId` (`string`)
- `bbox` (`BboxInfo`)
- `tokenIds` (`string[]`)
- `confidence` (`double`)
- `isLowConfidence` (`bool`)

Line semantics:

- A line groups token ids in reading order.
- Use line-token linkage to reconstruct per-line text or verify extraction context.
## 11. Blocks Reference

### `pages[].blocks[]`

- `blockId` (`string`)
- `type` (`string`)
- `bbox` (`BboxInfo`)
- `lineIds` (`string[]`)
- `tokenIds` (`string[]`)
- `confidence` (`double`)
- `isLowConfidence` (`bool`)

Block semantics:

- Blocks are higher-level groupings of lines/tokens (currently text blocks in implementation).

## 12. Tables Reference

### `pages[].tables[]`

- `tableId` (`string`)
- `confidence` (`double`)
- `bbox` (`BboxInfo`)
- `detection` (`TableDetectionInfo`)
- `grid` (`TableGridInfo`)
- `header` (`TableHeaderInfo`)
- `cells` (`TableCellInfo[]`)
- `rows` (`TableRowInfo[]`)
- `tokenCoverage` (`TableTokenCoverageInfo`)
- `issues` (`IssueInfo[]`)

### `detection`

- `method` (`string`)
- `hasExplicitGridLines` (`bool`)
- `modelName`, `modelVersion` (`string?`)
- `notes` (`string[]`)

### `grid`

- `rows`, `cols` (`int`)
- `rowBands[]` (`rowIndex`, `type`, `bbox`)
- `colBands[]` (`colIndex`, `bbox`)

### `header`

- `rowIndex` (`int`)
- `columns[]` (`colIndex`, `name`, `key`, `bbox`, `confidence`)
- `cells[]` (`rowIndex`, `colIndex`, span/text/confidence/bbox/tokenIds)

### `cells[]` (data cells)

- `rowIndex`, `colIndex`, `rowSpan`, `colSpan` (`int`)
- `text` (`string`)
- `normalized` (`type`, `value`, `currency?`, `unit?`)
- `confidence` (`double`)
- `bbox` (`BboxInfo`)
- `tokenIds` (`string[]`)
- `isLowConfidence` (`bool`)

Table text semantics:

- `text` preserves multiline table cell content when OCR/layout identifies stacked lines inside one cell.
- Current implementation serializes preserved table line breaks as embedded `\r\n`.
- `normalized.value` remains string-typed for multiline text cells and preserves the same line-break content.

### `rows[]` (derived row view)

- `rowIndex` (`int`)
- `type` (`string`)
- `values` (`Dictionary<string, object?>`)
- `source.cellRefs[]` (`rowIndex`, `colIndex`)
- `confidence` (`double`)
- `isLowConfidence` (`bool`)

### `tokenCoverage`

- `tokenCountInCells` (`int`)
- `tokenCountOverlappingTableBbox` (`int`)
- `coverageRatio` (`double`)

Consumer notes:

- `rows[].values` is convenient for business use.
- `cells[].tokenIds` and bbox fields preserve traceability back to OCR evidence.
- Consumers that need one-line exports should flatten `rows[].values` or `cells[].text` explicitly; the contract does not guarantee single-line table text.

## 13. KeyValueCandidates Reference

### `pages[].keyValueCandidates[]`

- `pairId` (`string`)
- `label` (`KeyValuePartInfo`)
- `value` (`KeyValuePartInfo`)
- `confidence` (`double`)
- `method` (`string`)

### `KeyValuePartInfo`

- `text` (`string`)
- `confidence` (`double`)
- `bbox` (`BboxInfo`)
- `tokenIds` (`string[]`)

Note:

- These are candidate detections, not guaranteed final business fields.

## 14. Regions Reference

### `pages[].regions[]`

- `regionId` (`string`)
- `type` (`string`, for example checkbox/radio)
- `bbox` (`BboxInfo`)
- `confidence` (`double`)
- `value` (`bool?`)
- `labelTokenIds` (`string[]`)
- `notes` (`string[]`)

Use:

- Form control detection and downstream option/boolean workflows.
- Traceability via region bbox and linked label token ids.

## 15. Words / DocumentWords / PageWords Reference

Present in current contract:

- `documentWords` (`string[]`) at top level.
- `pages[].pageWords` (`string[]`) per page.

Meaning:

- Unique word lists derived from OCR tokens.

Consumer use:

- indexing/search
- vocabulary analytics
- quick content summaries

## 16. Recognition Section Reference

### `recognition`

- `documentType` (`DocumentTypeInfo`)
- `anchors` (`AnchorsInfo`)
- `fields` (`RecognitionFieldInfo[]`)
- `tableMappings` (`object[]`)

### `documentType`

- `name` (`string?`)
- `version` (`string?`)
- `confidence` (`double`)

### `anchors`

- `matchedAnchors` (`object[]`)
- `unmatchedAnchors` (`object[]`)

### `tableMappings`

- `object[]` placeholder-style mapping container.

## 17. Recognition Fields Reference

`recognition.fields[]` is additive structured interpretation.

### `RecognitionFieldInfo`

- `fieldId` (`string`)
- `label` (`string`)
- `type` (`string`)
- `value` (`object?`)
- `normalized` (`TableCellNormalizedInfo`)
- `confidence` (`double`)
- `isLowConfidence` (`bool`)
- `source` (`FieldSourceInfo`)
- `validation` (`FieldValidationInfo`)
- `review` (`FieldReviewInfo`)

### `source`

- `pageIndex` (`int`)
- `bbox` (`BboxInfo`)
- `tokenIds` (`string[]`)
- `method` (`string`)

### `validation`

- `rulesApplied` (`string[]`)
- `validated` (`bool`)
- `issues` (`IssueInfo[]`)

### `review`

- `needsReview` (`bool`)
- `reason` (`string?`)

Consumer guidance:

- Keep using raw evidence for traceable source context.
- Use `source.tokenIds` and `source.bbox` to link fields back to OCR evidence.

## 18. Review Section Reference

### `review`

- `required` (`bool`)
- `reviewedBy` (`string?`)
- `reviewedUtc` (`string?`)
- `changes` (`object[]`)

Current role:

- Review-state container; often default/empty unless explicitly populated by upstream workflow.

## 19. Warnings and Errors Reference

### `warnings[]` and `errors[]`

Both are arrays of `IssueInfo`:

- `code` (`string`)
- `severity` (`string`)
- `message` (`string`)
- `pageIndex` (`int?`)
- `details` (`Dictionary<string, object?>`)

Interpretation:

- `errors[]`: error conditions detected during processing.
- `warnings[]`: warning/info diagnostics and quality signals.
- Partial-success handling is expected: usable evidence may exist even with warnings/errors.

## 20. Extensions Section Reference

`extensions` contains additive diagnostics and run metadata.

### `extensions` fields

- `tessdataPath` (`string`)
- `optionSnapshot` (`OptionSnapshotInfo`)
- `pagePreprocessing` (`PagePreprocessingInfo[]`)
- `debugArtifactPaths` (`PageDebugArtifactInfo[]`)
- `pageNoiseDiagnostics` (`PageNoiseDiagnosticsInfo[]`)
- `lineReconstructionDiagnostics` (`LineReconstructionDiagnosticsInfo[]`)
- `tokenCleanupStats` (`TokenCleanupStatsInfo[]`)
- `filteredTokenIds` (`string[]`)
- `fieldExtractionDiagnostics` (`FieldExtractionDiagnosticsInfo[]`)
- `structuredFieldExtractionStats` (`StructuredFieldExtractionStatsInfo`)
- `pipelineStageTimings` (`PipelineStageTimingInfo[]`)

### Notable extension sub-shapes

- `optionSnapshot`: normalized run options used.
- `pageDebugArtifactInfo`: file paths for render/preprocess/overlay images.
- `structuredFieldExtractionStats`: pages processed, additive candidate/field counts.
- `pipelineStageTimingInfo`: stage name, duration, status, note.

Note:

- Extensions are useful for debugging/operations and may be less stable than core evidence structures.

## 21. Data Type / Shape Guidance

Common shapes in this contract:

- Scalar:
  - `string`, `int`, `double`, `bool`
- Nullable:
  - `string?`, `bool?`, `double?`, `object?`
- Arrays:
  - `string[]`, object arrays, typed object arrays
- Nested objects:
  - section-specific models (`document`, `metrics`, `page`, etc.)
- Bbox object:
  - `{ x:int, y:int, w:int, h:int }`
- Traceability references:
  - `tokenIds` arrays linking derived structures back to raw tokens

## 22. Consumer Guidance

Recommended safe consumption patterns:

- Treat `tokens/lines/blocks/tables/regions` as source evidence.
- Treat `recognition.fields` and `keyValueCandidates` as additive interpretation.
- Use confidence values and review flags for trust decisions.
- Check `warnings[]` and `errors[]` on every run.
- Do not assume all sections are always populated.
- Preserve traceability with `pageIndex`, `bbox`, and `tokenIds`.
- Do not assume table string values are single-line; preserve or normalize embedded line breaks based on your workflow.

## 23. Example Usage Notes

Practical retrieval patterns:

- Find all OCR words on a page:
  - iterate `pages[n].tokens[].text`
- Get table rows for export:
  - iterate `pages[n].tables[m].rows[].values`
- Find checkbox/radio detections:
  - iterate `pages[n].regions[]` and filter by `type`
- Read structured fields:
  - iterate `recognition.fields[]`
- Inspect performance:
  - read `metrics` + `extensions.pipelineStageTimings[]`

## 24. Summary

The OCR JSON contract is organized as evidence-first output with additive interpretation, plus metrics and diagnostics. Consumers should rely on raw evidence for traceability, use structured sections as convenience layers, and always evaluate warnings/errors and confidence metadata when building downstream automation.
