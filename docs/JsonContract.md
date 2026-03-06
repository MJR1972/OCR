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
- Low-confidence logic is planned for later phases; Phase 1 uses placeholders.

## ID Determinism Rules (Phase 2+)
- IDs will be deterministic within a run and stable for reproducible inputs.
- Planned conventions:
  - `page-{index}` for pages
  - `t-######` for tokens
  - `ln-####` for lines
  - `blk-####` for blocks
  - `tbl-####` for tables
  - `kv-####` for key-value pairs
- Deterministic ordering strategy will be defined with OCR/layout implementation in Phase 2.