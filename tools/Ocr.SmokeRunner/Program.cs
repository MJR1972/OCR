using Newtonsoft.Json.Linq;
using Ocr.Core.Models;
using Ocr.Core.Services;

var processor = new OcrProcessor();

var filePath = args.Length > 0 ? args[0] : string.Empty;
if (string.IsNullOrWhiteSpace(filePath))
{
    Console.Write("Enter file path: ");
    filePath = Console.ReadLine() ?? string.Empty;
}

var result = processor.ProcessFile(filePath, new OcrOptions());

if (!IsContractValid(result.Json))
{
    Console.Error.WriteLine("Contract validation failed. Required fields are missing.");
    if (!string.IsNullOrWhiteSpace(result.OutputJsonPath))
    {
        Console.Error.WriteLine($"Output JSON: {result.OutputJsonPath}");
    }

    return 1;
}

Console.WriteLine("Smoke validation passed.");
Console.WriteLine($"Output JSON: {result.OutputJsonPath ?? "(not written)"}");
return 0;

static bool IsContractValid(string json)
{
    try
    {
        var root = JObject.Parse(json);
        var hasSchemaName = root["schema"]?["name"] is JValue;
        var hasSchemaVersion = root["schema"]?["version"] is JValue;
        var hasDocumentId = root["document"]?["documentId"] is JValue;
        var hasPagesArray = root["pages"] is JArray;

        return hasSchemaName && hasSchemaVersion && hasDocumentId && hasPagesArray;
    }
    catch
    {
        return false;
    }
}