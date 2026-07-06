# Changelog

All notable changes to `AetherDb.Sdk` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0]

### Added

- **Move a document between partitions.**
  `AetherClient.MoveDocumentAsync(docId, fromPartition, toPartition)` relocates a
  document from one hard partition to another in a single call
  (`POST /v1/documents/{id}/move`). Optionally assert the document's current
  partition to guard against a concurrent move.
- **Analytical queries.** `AetherClient.QueryAsync(QueryRequest)` runs an exact,
  deterministic structured query over your declared typed fields and the built-in
  record fields — filter, sort, paginate (Mode A), or group and aggregate
  (Mode B). It never consults an embedding.
- **Field-schema facade.** `AetherClient.Schema` lets you declare, list, and delete
  the typed fields that `QueryAsync` filters, sorts, and aggregates over
  (`DeclareFieldsAsync` / `ListFieldsAsync` / `DeleteFieldAsync`). Listing a field
  reports its live coverage and mismatch counts.
- **`PartitionRequiredException`.** A multi-tenant key that makes an unscoped call
  now raises a typed `PartitionRequiredException` (a subclass of
  `AetherApiException`) so callers can catch the "scope this through a partition
  handle" case directly instead of inspecting the error code.
- **`Partition` on response models.** The partition a record belongs to is now
  echoed back on document, search-result, and insert response models.

### Changed

- **Partition guard now covers id-addressed operations.** On a partition-scoped
  handle (`client.Partition("...")`), operations that address a document by id —
  download, restore, delete, and move — are automatically pinned to that
  partition, matching the behavior of the collection-level operations.

[0.4.0]: https://github.com/quintessence-group/aether-sdk-dotnet/releases/tag/v0.4.0
