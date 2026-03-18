namespace OcrShowcase.Demo.Wpf.Services;

public interface IDemoStartupService
{
    DemoStartupPayload? TryLoadDemoStartup();
}

public sealed record DemoStartupPayload(
    string DisplaySourcePath,
    OcrDemoRunResult RunResult);
