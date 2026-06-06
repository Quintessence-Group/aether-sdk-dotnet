# Aether.Sdk

.NET SDK for the [Aether](https://aetherdb.ai) decentralized RAG API.

## Installation

```bash
dotnet add package Aether.Sdk
```

## Quick Start

```csharp
using Aether.Sdk;

using var client = new AetherClient(new AetherClientOptions
{
    ApiKey = "aether_your_key_here",
});

// Insert a file — content type is auto-detected from the filename
var bytes = File.ReadAllBytes("document.pdf");
var doc = await client.InsertAsync(bytes, "document.pdf");
Console.WriteLine($"Inserted: {doc.DocId}");

// Search
var results = await client.SearchAsync("machine learning", k: 5);
foreach (var r in results)
    Console.WriteLine($"  {r.DocId} (distance: {r.Distance:F3})");

// Insert raw text
var textDoc = await client.InsertTextAsync("Some text content to index");

// List documents
var docs = await client.ListAsync();
foreach (var d in docs)
    Console.WriteLine($"  {d.DocId}: {d.Title}");
```

## Supported File Formats

Content type is auto-detected from the filename extension. No need to specify it manually.

| Format | Extensions |
|--------|-----------|
| PDF | .pdf |
| Word | .docx, .doc |
| PowerPoint | .pptx, .ppt |
| Excel | .xlsx, .xls |
| HTML | .html, .htm |
| CSV | .csv |
| Plain text | .txt, .md, .json, .xml |

Binary-format parsing is handled automatically server-side — no setup required.

## Supported Platforms

- .NET 8.0+
- .NET Standard 2.0 (.NET Framework 4.6.1+, .NET Core 2.0+, Unity, Xamarin)

## License

MIT
