# Changelog

All notable changes to `AetherDb.Sdk` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.0]

Additive release — no breaking changes. Existing code continues to work
unchanged; every new method, model, and exception is opt-in.

### Added

- **Durable conversation threads.** `AetherClient.AppendThreadAsync` and
  `AetherClient.GetThreadAsync` store and replay an ordered message history
  (`ConversationThread`) for an agent or chat session; the server assigns each
  turn's index atomically. `Memory.Thread(threadId)` is the ergonomic facade,
  with `AppendAsync` and `ContextAsync`.
- **Thread lifecycle.** Whole-thread operations —
  `AetherClient.ThreadRestoreAsync(threadId)`,
  `AetherClient.ThreadAclAsync(threadId, aclReaders)`,
  `AetherClient.ThreadMoveAsync(threadId, expectPartition, toPartition)`, and
  `AetherClient.ThreadDeleteAsync(threadId, hard: false)` — each returning a
  uniform `ThreadLifecycleResult` (`Status`, `ThreadId`, `Turns`), with the same
  operations on the `Thread` facade as `RestoreAsync`, `SetAclAsync`,
  `MoveAsync`, and `DeleteAsync`. Every operation sends an `Idempotency-Key`
  (pass `idempotencyKey` to make cross-process retries safe). Turn text is
  never rewritten — an edit appends a correction turn — and deletes are soft by
  default; `hard: true` is an irreversible erasure.
- **Shared grounding provenance receipts.**
  `AetherClient.CreateGroundingReceiptAsync` and
  `AetherClient.RevokeGroundingReceiptAsync` bind a generated answer to its
  declared sources with signed evidence. New `GroundingReceipt`,
  `GroundingBinding`, `GroundingSource`, `GroundingTrustSignal`,
  `GroundingSetAttestation`, `ReceiptAttestation`, and `ShareableReceipt`
  models.
- **Multimodal memory.** `Memory.RememberImageAsync` / `RememberImageFileAsync`
  and `Memory.RememberAudioAsync` / `RememberAudioFileAsync` remember image and
  audio content so it is recalled alongside text (raw access via
  `AetherClient.RememberMediaAsync`). Media results surface as
  `MediaMemoryRecord`.
- **Connections API + connect sessions.** Attach an end user's external account
  (Dropbox today) to their partition from your own backend, without the portal:
  - `AetherClient.CreateConnectSessionAsync(externalUserId, returnUrl, targetPartition, provider)`
    mints a hosted OAuth entry point and returns a `ConnectSession`
    (`SessionToken`, `ConnectUrl`, a one-time `ClientSecret`, `ExpiresAt`).
  - `AetherConnections.VerifyRedirectSignature(clientSecret, session, status, connectionId, sig)`
    verifies the signed redirect back to your return URL entirely offline
    (HMAC-SHA256 over `session|status|connection_id`, keyed by
    `SHA-256(client_secret)`). Framework crypto only; no new dependencies.
  - `AetherClient.ListConnectionsAsync(ListConnectionsOptions?)`,
    `GetConnectionAsync`, `ResyncConnectionAsync`,
    `BrowseConnectionAsync(connectionId, path, cursor)`, and
    `UpdateSelectionAsync(connectionId, selectedPaths)` manage a connection and
    its sync scope. `DeleteConnectionAsync` purges the synced content and
    returns a `DisconnectResult`; the signed purge receipt is fetchable with
    `GetPurgeReceiptAsync(receiptId)` (`ConnectionPurgeReceipt`).
  - New models: `ConnectSession`, `Connection`, `ListConnectionsOptions`,
    `ConnectionBrowseEntry`, `ConnectionBrowsePage`, `DisconnectResult`,
    `PurgeSummary`, `ConnectionPurgeReceipt`.
- **Typed connect-session exceptions.** `SessionInvalidException` (HTTP 400,
  `code = "session_invalid"` — the session token is unknown, already used, or
  expired; mint a new session instead of retrying) and
  `PartitionMismatchException` (HTTP 400, `code = "partition_mismatch"` — the
  handle's partition disagrees with where the session would resolve). Both are
  subclasses of `AetherApiException`; neither is retryable.
- **`AetherDb.Sdk.SemanticKernel` 0.1.0 (new package).** `AetherMemoryProvider`,
  a Semantic Kernel `AIContextProvider` backed by this SDK's `Memory` facade:
  attach it to an agent thread and each user turn is remembered while the most
  relevant memories are recalled and injected before every model turn.
  Configure with `AetherMemoryProviderOptions` (`K`, `RecencyWeight`,
  `MinRelevance`, `ContextPrompt`, `ExtractFacts`, `IncludeAssistantMessages`).
  Targets `netstandard2.0` and `net8.0`; depends on `AetherDb.Sdk >= 0.6.0`.

### Changed

- **User-Agent reports the real SDK version.** The version sent in the
  `User-Agent` header had been stuck at `0.3.2`; it now tracks the package
  version.

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

[0.6.0]: https://github.com/quintessence-group/aether-sdk-dotnet/releases/tag/v0.6.0
[0.4.0]: https://github.com/quintessence-group/aether-sdk-dotnet/releases/tag/v0.4.0
