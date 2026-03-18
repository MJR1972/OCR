# OCR

Phase 1 scaffolds a .NET 10 OCR library, a WPF MVVM test harness, and a console smoke runner.

## Prerequisites
- Windows
- .NET SDK 10.0+

## Build
```powershell
dotnet restore OCR.sln
dotnet build OCR.sln -c Debug
```

## Run WPF Harness
```powershell
dotnet run --project src/Ocr.TestHarness.Wpf/Ocr.TestHarness.Wpf.csproj
```

Workflow:
1. Browse and select a supported file path (`.pdf`, `.jpg`, `.jpeg`, `.png`, `.tif`, `.tiff`, `.gif`, `.bmp`).
2. Optionally change output folder/options.
3. Click **Run**.

The harness calls `OcrProcessor` and displays placeholder JSON.

## Internal Architecture
The DLL now uses an internal staged pipeline runner for maintainability while preserving public behavior and JSON compatibility.

Developer notes:
- [docs/InternalPipeline.md](docs/InternalPipeline.md)
- [docs/OcrDllRequirements.md](docs/OcrDllRequirements.md)
- [docs/OcrTestHarnessRequirements.md](docs/OcrTestHarnessRequirements.md)
- [docs/OcrSystemTestCases.md](docs/OcrSystemTestCases.md)
- [docs/OcrDllIntegrationGuide.md](docs/OcrDllIntegrationGuide.md)
- [docs/OcrJsonContractReference.md](docs/OcrJsonContractReference.md)

## Run Smoke Runner
```powershell
dotnet run --project tools/Ocr.SmokeRunner/Ocr.SmokeRunner.csproj -- "C:\path\to\input.pdf"
```
If no argument is provided, it prompts for a path.

## Output Location
When `SaveJsonToDisk` is enabled, output is written to:
- `Path.Combine(AppContext.BaseDirectory, "output")` when no output folder is provided
- Subfolder format: `{fileNameNoExt}_{yyyyMMdd_HHmmss}`
- File name: `result.json`

## Tessdata Expectation
`OcrProcessor` records tessdata location as:
- `Path.Combine(AppContext.BaseDirectory, "tessdata")`

Phase 1 does not fail if `tessdata` is missing.
