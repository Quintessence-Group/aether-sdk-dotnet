# Aether.Sdk

.NET SDK for the [Aether](https://aetherdb.ai) decentralized RAG API.

## Installation

```bash
dotnet add package AetherDb.Sdk
```

The package installs as `AetherDb.Sdk`; the assembly and namespace stay `Aether.Sdk`
(`using Aether.Sdk;`).

## Memory — the fastest way to build agent memory

For per-user or per-agent memory, reach for the `Memory` facade. Construct it once
with an entity id and every call is automatically scoped to that entity — no tags or
filters to manage:

```csharp
using Aether.Sdk;

using var mem = new Memory("patient-john", new MemoryOptions
{
    ApiKey = "aether_your_key_here",
});

// Store a memory
await mem.RememberAsync("Anxious about flying; uses 4-7-8 breathing");

// Recall the most relevant memories for this entity
foreach (var item in await mem.RecallAsync("anxiety coping"))
    Console.WriteLine($"{item.Score}  {item.Text}");

// Newest-first history, or wipe the slate
await mem.ListAsync(limit: 20);
await mem.ForgetAllAsync();
```

- `RecallAsync(query, k: 5, recencyWeight: 0.0, since: ..., until: ...)` blends
  relevance (a calibrated `score`, 0–100, normalized to `[0, 1]`) with optional
  exponential recency decay.
- `RememberAsync(text, metadata)` stores the memory and writes `metadata` as
  searchable `key:value` tags (write-only in v1 — it cannot be read back).
- Every operation is `async`; pass a `CancellationToken` to any call. Inject your
  own `AetherClient` with `new Memory(entityId, client)` to share one client across
  many entities.

The raw `AetherClient` below is the lower-level API — use it when you need direct
control over documents, search, and batch operations rather than entity-scoped memory.

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
    Console.WriteLine($"  {r.DocId} (score: {r.Score})");

// Insert raw text
var textDoc = await client.InsertTextAsync("Some text content to index");

// List documents
var listing = await client.ListAsync();
foreach (var d in listing.Documents)
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
