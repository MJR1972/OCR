# OcrShowcase.Demo.Wpf Summary

**Project:** OCR Showcase Demo  
**Status:** Current implementation summary  
**Last Updated:** 2026-03-18

## Overview

`OcrShowcase.Demo.Wpf` is the polished demo application in the solution for portfolio captures, stakeholder walkthroughs, and OCR contract inspection. It is a consumer of `Ocr.Core` and focuses on presentation, preview overlays, readable extracted summaries, JSON inspection, and logs.

## Current Scope

- Load a supported image or PDF document.
- Run OCR through `OcrProcessor`.
- Preview rendered pages with overlay toggles for words, fields, and tables.
- Inspect summary metrics, recognized fields, raw JSON, detected tables, and logs.
- Export the current OCR JSON payload.
- Review multiline table cells in the Tables tab with wrapped display content.

## Current UI Areas

- Header actions:
  - `Load Document`
  - `Run OCR`
  - `Export JSON`
- Preview surface:
  - page navigation
  - zoom controls
  - `Show Words`, `Show Fields`, `Show Tables`
- Result tabs:
  - `Summary`
  - `Fields`
  - `JSON`
  - `Tables`
  - `Log`
- Footer summary:
  - selected document
  - page count
  - elapsed time
  - mean confidence

## Implementation Notes

- Built on WPF and MVVM.
- `MainWindowViewModel` orchestrates document selection, OCR execution, summary projection, JSON formatting, preview updates, and table/field/log presentation.
- Preview rendering is page-aware and uses artifact/overlay projection services.
- The Tables tab is driven from DLL JSON and now preserves multiline table cell values instead of flattening them to one continuous line.
- OCR cleanup and table formatting logic remain in `Ocr.Core`; the showcase app only formats returned data for presentation.

## Current Data Presentation Behavior

- Summary cards and detail lists are projected from deserialized OCR contract data.
- Fields are displayed from `recognition.fields`.
- JSON is shown in a formatted read-only viewer.
- Tables are built from `pages[].tables[]`.
  - `rows[].values` is used when available.
  - raw cell text is used as a fallback.
  - multiline values are preserved and wrapped for readability.
- Log entries include pipeline timings, warnings, errors, overlay diagnostics, and export path details.

## Current Project Characteristics

- Project type: WPF application
- Target framework: `net10.0-windows`
- Primary dependency: `Ocr.Core`
- Purpose: polished demo/inspection surface, not the OCR logic owner

## Summary

The showcase application is no longer a phase-1 shell. It is a working OCR demo client with page preview, overlay controls, structured summaries, JSON export, field inspection, log diagnostics, and multiline-aware table presentation backed by the current `Ocr.Core` pipeline.
