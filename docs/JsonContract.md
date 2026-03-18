# JSON Contract Notes

## Coordinate Rules
- Coordinate unit is pixels (`px`).
- Origin is top-left.
- X increases to the right.
- Y increases downward.
- Bounding box format is `x,y,w,h`.
- Coordinates are relative to the final processed page image.

## Confidence Scale Rules
- All confidence values use a `0..1` scale.
- Threshold fields are present in `definitions.confidence` and `document.processing.confidencePolicy`.
- Low-confidence flags and thresholds are populated in current output for tokens, lines, cells, tables, and fields where applicable.

## ID Determinism Rules
- IDs will be deterministic within a run and stable for reproducible inputs.
- Current conventions:
  - `page-{index}` for pages
  - `t-{page:000}-{index:000000}` for tokens
  - `ln-{page:000}-{index:00000}` for lines
  - `blk-{page:000}-{index:00000}` for blocks
  - `tbl-####` for tables
  - `kv-{page:000}-{index:0000}` for key-value pairs

## Table Text Rules
- `pages[].tables[].cells[].text` preserves multiline table content using embedded `\r\n` line breaks when OCR/layout indicates stacked lines inside a cell.
- `pages[].tables[].rows[].values` preserves the same multiline text for string-valued cells.
- `normalized.value` for string table cells preserves multiline content; consumers that need flat export should normalize line breaks explicitly rather than assuming one-line table text.

## Token Cleanup Rules
- Token cleanup runs before line reconstruction and includes spacing normalization for common OCR glue artifacts.
- Current cleanup behavior includes preserving interword spacing where available, splitting common merged patterns such as `Thisistest#1` into `This is test # 1`, and collapsing repeated whitespace without removing intentional table cell line breaks.
