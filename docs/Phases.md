# Implementation Phases

## Phase 1
- Solution scaffolding and clean project structure
- `Ocr.Core` API surface and placeholder JSON contract output
- WPF MVVM test harness for file selection, options, run, and JSON/log display
- Console smoke runner without extra test framework packages

## Phase 2
- PDF rendering pipeline
- Preprocessing
- Tesseract OCR integration
- Populate tokens, lines, blocks, and full text

## Phase 3
- Harness preview and overlay rendering
- Optional debug artifacts persisted to disk

## Phase 4+
- Table extraction
- Key-value extraction
- Region detection
- Recognition and higher-level field mapping