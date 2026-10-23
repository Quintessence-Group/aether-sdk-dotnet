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

### Memory files

Each entity can also keep named, versioned **markdown files** — for example a
`memory.md` that an agent reads at the start of a session and keeps up to date. Read a
file whole, one section at a time, or page by page; replace it whole under a version
precondition, or edit it section by section; list its versions, diff them, and grep
across files:

```csharp
var files = mem.Files;

// Section-aware edits need no precondition: they apply to the current version.
// A missing file or section is created.
await files.AppendSectionAsync("memory.md", "Agreed terms", "- 2026-10-10 — Net 45 for EU entities");

// Read the whole file, one section (heading text or slug), or just the outline.
var file = await files.ReadAsync("memory.md");
var terms = await files.ReadAsync("memory.md", section: "Agreed terms");
foreach (var s in file.Sections)
    Console.WriteLine($"{new string('#', s.Level)} {s.Heading} (lines {s.StartLine}-{s.EndLine})");

// History: retained versions (newest first) and a unified diff.
var history = await files.VersionsAsync("memory.md");
var diff = await files.DiffAsync("memory.md"); // current vs. the previous version
Console.WriteLine(diff.Diff);

// Line-based search across this entity's files (literal by default; regex: true for patterns).
var found = await files.GrepAsync("net 45", ignoreCase: true);
foreach (var m in found.Matches)
    Console.WriteLine($"{m.Name}:{m.Line} [{m.Section}] {m.Text}");

// Soft delete (re-creating the name continues its versions), or hard delete for erasure.
await files.DeleteAsync("scratch.md", hard: true);
```

**Read-modify-write.** To replace a file whole, read it, edit the content, and write it
back with the version you read. If another writer got there first, the write fails with
`VersionConflictException` (its `CurrentVersion` says what is there now); re-read and
retry:

```csharp
while (true)
{
    var current = await files.ReadAsync("memory.md");
    var edited = current.Content.Replace("Net 30", "Net 45");
    try
    {
        await files.WriteAsync("memory.md", edited, expectedVersion: current.Version);
        break;
    }
    catch (VersionConflictException)
    {
        // Someone else wrote first: loop to re-read and re-apply the edit.
    }
}

// Create-only: fails with VersionConflictException if the file already exists.
await files.WriteAsync("notes.md", "# Notes\n", create: true);
```

- `WriteAsync` and `ReplaceSectionAsync` take exactly one of `expectedVersion` or
  `create: true`; passing neither or both throws `ArgumentException` before any request.
  `AppendAsync` and `AppendSectionAsync` need no precondition (pass `expectedVersion`
  to make them conditional too).
- Every write sends an `Idempotency-Key` that the SDK reuses across its automatic retries
  (pass `idempotencyKey:` to choose it), so a retried append is never applied twice.
  Writing identical content is a no-op (`Unchanged`).
- Names are 1–128 letters, digits, `.`, `_` or `-`, starting with a letter or digit;
  `MemoryFiles.ValidateName` checks one up front.
- A 404 `AetherApiException` with `ErrorCode == "feature_not_enabled"` means memory files
  aren't enabled on your deployment.

Across entities, the raw client lists and greps files by entity-id prefix — for example
every company in one workspace:

```csharp
var page = await client.ListMemoryFilesAsync(entityPrefix: "ws_42:company:");
var hits = await client.GrepMemoryFilesAsync("net 45", entityPrefix: "ws_42:company:", ignoreCase: true);
```

Search hits on a memory file carry `MemoryFile` (`Name`, `Version`, `Section`).

### Reading a document's text

`GetTextAsync` returns the parsed, readable text of any stored document — the
original for text types, the caption or transcript for media, and text rebuilt
from the parsed passages for PDF/Office — paged and addressable by section:

```csharp
var outline = await client.GetTextAsync(doc.DocId, outline: true);
foreach (var s in outline.Sections!)
    Console.WriteLine(s.Heading);

var terms = await client.GetTextAsync(doc.DocId, section: "Payment terms");

var page = await client.GetTextAsync(doc.DocId, maxBytes: 65536);
while (page.Truncated)
    page = await client.GetTextAsync(doc.DocId, offset: page.NextOffset, maxBytes: 65536);
```

### Document summaries

Ask for a summary at ingest and the server generates it in the background once the
document is stored — it never fails or delays the insert:

```csharp
var msa = await client.InsertAsync(File.ReadAllBytes("msa.pdf"), "msa.pdf", summarize: true);
Console.WriteLine(msa.Summary); // "pending", or "skipped" by policy

var summary = await client.GetSummaryAsync(msa.DocId);
if (summary.State == "ready")
    Console.WriteLine(summary.Content);

// Queue a fresh one, or store your own (optionally only if it hasn't changed since you read it).
await client.RegenerateSummaryAsync(msa.DocId);
await client.PutSummaryAsync(msa.DocId, "## Summary\n- Our notes.\n", expectedRevision: summary.Revision);
```

- `summarize: null` (the default) applies your tenant's default; `true` or `false`
  overrides it. It is accepted by `InsertAsync`, `InsertTextAsync`, `InsertStreamAsync`,
  `EnqueueDocumentAsync` (the finished job reports `SummaryState`) and `BatchInsertAsync`
  (batch-wide, plus a per-item `BatchInsertItem.Summarize`). On `Memory`, set
  `MemoryOptions.Summarize` or pass `summarize:` to `RememberAsync`.
- `State` is `none`, `pending`, `ready`, `failed`, `skipped` or `unsupported`.
  `Content` shows the stored summary whatever the state, and `Stale` is true when the
  document changed after its summary was written. If you asked for a summary and later
  read `none`, the generation was interrupted: call `RegenerateSummaryAsync`.
- A summary you wrote is kept until you replace it; `RegenerateSummaryAsync(docId, force: true)`
  replaces it with a generated one.
- A 404 `feature_not_enabled` means summaries aren't enabled on your deployment.
