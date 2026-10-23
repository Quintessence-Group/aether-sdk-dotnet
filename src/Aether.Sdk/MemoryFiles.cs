using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Aether.Sdk;

/// <summary>
/// Named, versioned markdown files for one entity — the <see cref="Memory.Files"/>
/// facade. Read a file whole, by section or by page; replace it whole under a
/// version precondition, or edit it section by section; list its versions, diff
/// them, grep across files, and delete.
/// </summary>
/// <remarks>
/// <para>
/// Every call is scoped to the owning <see cref="Memory"/>'s entity and goes
/// through its raw client, so a partition handle, retries and the typed errors
/// apply unchanged. A 404 <c>feature_not_enabled</c> <see cref="AetherApiException"/>
/// means memory files aren't enabled on your deployment.
/// </para>
/// <para>
/// <b>Read-modify-write:</b> <see cref="ReadAsync"/>, edit the content, then
/// <see cref="WriteAsync"/> with <c>expectedVersion: file.Version</c>; on a
/// <see cref="VersionConflictException"/>, re-read and retry.
/// <see cref="AppendAsync"/> and <see cref="AppendSectionAsync"/> need no
/// precondition: they apply atomically to whatever the current version is.
/// Every write carries an <c>Idempotency-Key</c> reused across the SDK's
/// automatic retries, so a retried write is never applied twice.
/// </para>
/// </remarks>
public sealed class MemoryFiles
{
    private static readonly HttpMethod Patch = new("PATCH");

    private readonly AetherClient _client;
    private readonly string _entityId;

    internal MemoryFiles(AetherClient client, string entityId)
    {
        _client = client;
        _entityId = entityId;
    }

    // ── Validation ────────────────────────────────────────────────────

    /// <summary>
    /// Check a memory-file name: 1–128 ASCII letters, digits, <c>.</c>, <c>_</c> or
    /// <c>-</c>, starting with a letter or digit, not containing <c>..</c>, and not
    /// exactly <c>grep</c>. Names are case-sensitive.
    /// </summary>
    /// <param name="name">The file name to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid memory-file name.</exception>
    public static void ValidateName(string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        if (!IsValidName(name))
            throw new ArgumentException(
                $"invalid memory file name \"{name}\": use 1-128 letters, digits, '.', '_' or '-', " +
                "starting with a letter or digit, without \"..\", and not \"grep\"",
                nameof(name));
    }

    // Explicit character checks over the whole string (no regex anchors, so a
    // trailing newline can never slip through).
    private static bool IsValidName(string name)
    {
        if (name.Length < 1 || name.Length > 128 || !IsAsciiAlphanumeric(name[0]))
            return false;
        foreach (var c in name)
        {
            if (!IsAsciiAlphanumeric(c) && c != '.' && c != '_' && c != '-')
                return false;
        }
        return name.IndexOf("..", StringComparison.Ordinal) < 0 && name != "grep";
    }

    private static bool IsAsciiAlphanumeric(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private static void RequireContent(string content, bool allowEmpty)
    {
        if (content is null)
            throw new ArgumentNullException(nameof(content));
        if (!allowEmpty && content.Length == 0)
            throw new ArgumentException("content cannot be empty", nameof(content));
    }

    private static void RequireSection(string section)
    {
        if (section is null)
            throw new ArgumentNullException(nameof(section));
        if (string.IsNullOrWhiteSpace(section))
            throw new ArgumentException("section cannot be empty or whitespace", nameof(section));
    }

    private static void RequireExpectedVersion(int? expectedVersion)
    {
        if (expectedVersion is < 1)
            throw new ArgumentOutOfRangeException(
                nameof(expectedVersion), expectedVersion, "expectedVersion must be at least 1");
    }

    // write / replace_section: exactly one of If-Match: "<v>" or If-None-Match: *.
    private static KeyValuePair<string, string>[] ExactlyOnePrecondition(int? expectedVersion, bool create)
    {
        if (expectedVersion.HasValue == create)
            throw new ArgumentException("pass exactly one of expectedVersion or create", nameof(expectedVersion));
        if (create)
            return new[] { new KeyValuePair<string, string>("If-None-Match", "*") };
        RequireExpectedVersion(expectedVersion);
        return new[] { IfMatch(expectedVersion!.Value) };
    }

    // append / append_section / delete: If-Match only when a version is given.
    private static KeyValuePair<string, string>[]? OptionalIfMatch(int? expectedVersion)
    {
        RequireExpectedVersion(expectedVersion);
        return expectedVersion.HasValue ? new[] { IfMatch(expectedVersion.Value) } : null;
    }

    private static KeyValuePair<string, string> IfMatch(int version) =>
        new("If-Match", "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"");

    /// <summary>
    /// Parse a memory-file <c>ETag</c> (<c>"7"</c>, also a bare <c>7</c> or a weak
    /// <c>W/"7"</c>) into its version; null when missing or not a decimal integer.
    /// </summary>
    internal static int? ParseETagVersion(string? etag)
    {
        if (etag is null)
            return null;
        var tag = etag.Trim();
        if (tag.StartsWith("W/", StringComparison.Ordinal))
            tag = tag.Substring(2);
        if (tag.Length >= 2 && tag[0] == '"' && tag[tag.Length - 1] == '"')
            tag = tag.Substring(1, tag.Length - 2);
        return int.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    // ── Query helpers ─────────────────────────────────────────────────

    private List<string> EntityQuery()
    {
        return new List<string> { "entity_id=" + Uri.EscapeDataString(_entityId) };
    }

    private static string FilePath(string name, string? suffix = null) =>
        "/memory/files/" + Uri.EscapeDataString(name) + (suffix ?? "");

    private static void AddAuthor(List<string> query, string? author)
    {
        if (!string.IsNullOrEmpty(author))
            query.Add("author=" + Uri.EscapeDataString(author!));
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    // ── List / read ───────────────────────────────────────────────────

    /// <summary>List this entity's current memory files, ordered by name.</summary>
    /// <param name="prefix">Only files whose name starts with this (case-sensitive).</param>
    /// <param name="limit">Page size, 1–1000 (server default 100).</param>
    /// <param name="cursor">A previous page's <see cref="MemoryFilePage.NextCursor"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MemoryFilePage> ListAsync(
        string? prefix = null,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        _client.ListMemoryFilesAsync(
            entityId: _entityId, prefix: prefix, limit: limit, cursor: cursor,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Read a memory file: the current version (or an older retained one), whole,
    /// one section, an outline, or a page.
    /// </summary>
    /// <remarks>
    /// <see cref="MemoryFile.Sections"/> is always the whole version's outline.
    /// Page through a large file by passing <see cref="MemoryFile.NextOffset"/>
    /// back as <paramref name="offset"/> while <see cref="MemoryFile.Truncated"/>.
    /// </remarks>
    /// <param name="name">The file name.</param>
    /// <param name="version">Read this retained older version instead of the current one.</param>
    /// <param name="section">A heading's text (case- and whitespace-insensitive) or its exact slug; the result is that section including its heading.</param>
    /// <param name="outline">Return only the outline, with empty content.</param>
    /// <param name="offset">Byte offset to start from — a previous page's <see cref="MemoryFile.NextOffset"/>.</param>
    /// <param name="maxBytes">Page budget in bytes, 1–524288 (default: the whole file).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is invalid (no request is sent).</exception>
    /// <exception cref="AetherApiException">404 (no such file, version or section), 409 (ambiguous section).</exception>
    public async Task<MemoryFile> ReadAsync(
        string name,
        int? version = null,
        string? section = null,
        bool outline = false,
        int? offset = null,
        int? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var query = EntityQuery();
        if (version.HasValue)
            query.Add("version=" + Int(version.Value));
        if (section != null)
            query.Add("section=" + Uri.EscapeDataString(section));
        if (outline)
            query.Add("outline=true");
        if (offset.HasValue)
            query.Add("offset=" + Int(offset.Value));
        if (maxBytes.HasValue)
            query.Add("max_bytes=" + Int(maxBytes.Value));
        var (file, etag) = await _client.SendMemoryFileAsync<MemoryFile>(
            HttpMethod.Get, FilePath(name), query, null, null, null, cancellationToken).ConfigureAwait(false);
        if (file.Version == 0 && ParseETagVersion(etag) is { } fromETag)
            file.Version = fromETag;
        return file;
    }

    // ── Writes ────────────────────────────────────────────────────────

    /// <summary>
    /// Replace the whole file. Pass exactly one of <paramref name="expectedVersion"/>
    /// (the version you read — the write fails with a
    /// <see cref="VersionConflictException"/> if the file has changed since) or
    /// <paramref name="create"/> = true (create-only: fails if the file exists).
    /// </summary>
    /// <remarks>
    /// Writing content identical to the current version is a no-op
    /// (<see cref="MemoryFileWriteResult.Unchanged"/>). Files hold up to 512 KiB.
    /// </remarks>
    /// <param name="name">The file name.</param>
    /// <param name="content">The new markdown content (non-empty).</param>
    /// <param name="expectedVersion">The current version you expect, 1 or more.</param>
    /// <param name="create">Create the file; fail if it already exists.</param>
    /// <param name="author">Optional free-text attribution label (1–128 chars), stored with the version.</param>
    /// <param name="idempotencyKey">Optional retry key; one is generated per call when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Invalid name, or not exactly one of <paramref name="expectedVersion"/> / <paramref name="create"/> (no request is sent).</exception>
    /// <exception cref="VersionConflictException">The precondition failed.</exception>
    public async Task<MemoryFileWriteResult> WriteAsync(
        string name,
        string content,
        int? expectedVersion = null,
        bool create = false,
        string? author = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        RequireContent(content, allowEmpty: true);
        var headers = ExactlyOnePrecondition(expectedVersion, create);
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        body.Headers.ContentType = new MediaTypeHeaderValue("text/markdown") { CharSet = "utf-8" };
        return await SendWriteAsync(HttpMethod.Put, name, body, headers, author, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Append <paramref name="content"/> to the end of the file (a newline is added
    /// first if the file doesn't end with one). Needs no precondition.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="content">Text to append (non-empty).</param>
    /// <param name="expectedVersion">Optional precondition: fail with a <see cref="VersionConflictException"/> unless this is the current version.</param>
    /// <param name="createIfMissing">Create the file when it doesn't exist (default true); otherwise a missing file is a 404.</param>
    /// <param name="author">Optional free-text attribution label.</param>
    /// <param name="idempotencyKey">Optional retry key; one is generated per call when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Invalid name or empty content (no request is sent).</exception>
    public async Task<MemoryFileWriteResult> AppendAsync(
        string name,
        string content,
        int? expectedVersion = null,
        bool createIfMissing = true,
        string? author = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        RequireContent(content, allowEmpty: false);
        var headers = OptionalIfMatch(expectedVersion);
        var body = new Dictionary<string, object?>
        {
            ["op"] = "append",
            ["content"] = content,
            ["create_if_missing"] = createIfMissing,
        };
        return await SendPatchAsync(name, body, headers, author, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Append <paramref name="content"/> under a section: after the last non-blank
    /// line of the section's own body, ahead of any sub-sections. A missing section
    /// is created at the end of the file (as a heading of <paramref name="level"/>)
    /// unless <paramref name="createIfMissing"/> is false. Needs no precondition.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="section">A heading's text (case- and whitespace-insensitive) or its exact slug.</param>
    /// <param name="content">Text to insert (non-empty).</param>
    /// <param name="level">Heading level (1–6) used only when the section is created. Default 2.</param>
    /// <param name="expectedVersion">Optional precondition: fail with a <see cref="VersionConflictException"/> unless this is the current version.</param>
    /// <param name="createIfMissing">Create a missing file and/or section (default true); otherwise they are a 404.</param>
    /// <param name="author">Optional free-text attribution label.</param>
    /// <param name="idempotencyKey">Optional retry key; one is generated per call when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Invalid name, blank section or empty content (no request is sent).</exception>
    /// <exception cref="AetherApiException">409 <c>memory_file_section_ambiguous</c> when the heading text matches several sections (use the slug).</exception>
    public async Task<MemoryFileWriteResult> AppendSectionAsync(
        string name,
        string section,
        string content,
        int level = 2,
        int? expectedVersion = null,
        bool createIfMissing = true,
        string? author = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        RequireSection(section);
        RequireContent(content, allowEmpty: false);
        var headers = OptionalIfMatch(expectedVersion);
        var body = new Dictionary<string, object?>
        {
            ["op"] = "append_section",
            ["section"] = section,
            ["content"] = content,
            ["level"] = level,
            ["create_if_missing"] = createIfMissing,
        };
        return await SendPatchAsync(name, body, headers, author, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replace a section's body (everything under its heading, nested sub-sections
    /// included; the heading itself is kept). A missing section is created at the
    /// end of the file unless <paramref name="createIfMissing"/> is false. Pass
    /// exactly one of <paramref name="expectedVersion"/> or <paramref name="create"/> = true.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="section">A heading's text (case- and whitespace-insensitive) or its exact slug.</param>
    /// <param name="content">The new section body; empty clears it.</param>
    /// <param name="expectedVersion">The current version you expect, 1 or more.</param>
    /// <param name="create">Create-only: fail if the file already exists.</param>
    /// <param name="level">Heading level (1–6) used only when the section is created. Default 2.</param>
    /// <param name="createIfMissing">Create a missing file and/or section (default true); otherwise they are a 404.</param>
    /// <param name="author">Optional free-text attribution label.</param>
    /// <param name="idempotencyKey">Optional retry key; one is generated per call when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Invalid name, blank section, or not exactly one of <paramref name="expectedVersion"/> / <paramref name="create"/> (no request is sent).</exception>
    /// <exception cref="VersionConflictException">The precondition failed.</exception>
    public async Task<MemoryFileWriteResult> ReplaceSectionAsync(
        string name,
        string section,
        string content,
        int? expectedVersion = null,
        bool create = false,
        int level = 2,
        bool createIfMissing = true,
        string? author = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        RequireSection(section);
        RequireContent(content, allowEmpty: true);
        var headers = ExactlyOnePrecondition(expectedVersion, create);
        var body = new Dictionary<string, object?>
        {
            ["op"] = "replace_section",
            ["section"] = section,
            ["content"] = content,
            ["level"] = level,
            ["create_if_missing"] = createIfMissing,
        };
        return await SendPatchAsync(name, body, headers, author, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<MemoryFileWriteResult> SendPatchAsync(
        string name,
        Dictionary<string, object?> body,
        KeyValuePair<string, string>[]? headers,
        string? author,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return SendWriteAsync(Patch, name, content, headers, author, idempotencyKey, cancellationToken);
    }

    private async Task<MemoryFileWriteResult> SendWriteAsync(
        HttpMethod method,
        string name,
        HttpContent content,
        KeyValuePair<string, string>[]? headers,
        string? author,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var query = EntityQuery();
        AddAuthor(query, author);
        // One key per call, reused by the transport on every retry, so a write
        // whose response was lost is never applied twice.
        var key = string.IsNullOrEmpty(idempotencyKey) ? Guid.NewGuid().ToString() : idempotencyKey!;
        using (content)
        {
            var (result, etag) = await _client.SendMemoryFileAsync<MemoryFileWriteResult>(
                method, FilePath(name), query, content, headers, key, cancellationToken).ConfigureAwait(false);
            if (result.Version == 0 && ParseETagVersion(etag) is { } fromETag)
                result.Version = fromETag;
            return result;
        }
    }

    // ── History ───────────────────────────────────────────────────────

    /// <summary>List a file's retained versions, newest first.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="limit">Maximum versions, 1–100 (server default 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is invalid (no request is sent).</exception>
    public async Task<MemoryFileVersions> VersionsAsync(
        string name,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var query = EntityQuery();
        if (limit.HasValue)
            query.Add("limit=" + Int(limit.Value));
        var (versions, _) = await _client.SendMemoryFileAsync<MemoryFileVersions>(
            HttpMethod.Get, FilePath(name, "/versions"), query, null, null, null, cancellationToken)
            .ConfigureAwait(false);
        return versions;
    }

    /// <summary>
    /// Unified diff between two versions of a file. Defaults compare the current
    /// version with the one before it; <paramref name="fromVersion"/> 0 is the empty
    /// file, so it shows a whole version as added.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="fromVersion">The older side, 0 or more (default: <paramref name="toVersion"/> − 1).</param>
    /// <param name="toVersion">The newer side, 1 or more (default: the current version).</param>
    /// <param name="context">Unchanged context lines around each change, 0–20. Default 3.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is invalid (no request is sent).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromVersion"/> &lt; 0 or <paramref name="toVersion"/> &lt; 1.</exception>
    public async Task<MemoryFileDiff> DiffAsync(
        string name,
        int? fromVersion = null,
        int? toVersion = null,
        int context = 3,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        if (fromVersion is < 0)
            throw new ArgumentOutOfRangeException(nameof(fromVersion), fromVersion, "fromVersion must be at least 0");
        if (toVersion is < 1)
            throw new ArgumentOutOfRangeException(nameof(toVersion), toVersion, "toVersion must be at least 1");
        var query = EntityQuery();
        if (fromVersion.HasValue)
            query.Add("from=" + Int(fromVersion.Value));
        if (toVersion.HasValue)
            query.Add("to=" + Int(toVersion.Value));
        query.Add("context=" + Int(context));
        var (diff, _) = await _client.SendMemoryFileAsync<MemoryFileDiff>(
            HttpMethod.Get, FilePath(name, "/diff"), query, null, null, null, cancellationToken)
            .ConfigureAwait(false);
        return diff;
    }

    // ── Grep / delete ─────────────────────────────────────────────────

    /// <summary>
    /// Search this entity's current memory files line by line. A literal substring
    /// by default; <paramref name="regex"/> enables linear-time regular expressions
    /// (no back-references or look-around). Each matching line is one match.
    /// </summary>
    /// <param name="pattern">The text or regex to find (non-empty).</param>
    /// <param name="regex">Treat <paramref name="pattern"/> as a regular expression.</param>
    /// <param name="ignoreCase">Case-insensitive matching.</param>
    /// <param name="files">Only these file names (unknown names are skipped).</param>
    /// <param name="context">Lines of context before and after each match, 0–10. Default 0.</param>
    /// <param name="maxMatches">Stop after this many matches, 1–1000. Default 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is empty (no request is sent).</exception>
    public Task<MemoryGrepResult> GrepAsync(
        string pattern,
        bool regex = false,
        bool ignoreCase = false,
        IReadOnlyList<string>? files = null,
        int context = 0,
        int maxMatches = 100,
        CancellationToken cancellationToken = default) =>
        _client.GrepMemoryFilesAsync(
            pattern, entityId: _entityId, regex: regex, ignoreCase: ignoreCase, files: files,
            context: context, maxMatches: maxMatches, cancellationToken: cancellationToken);

    /// <summary>
    /// Delete a file. The default soft delete hides it from list, read, grep and
    /// search; re-creating the name continues its version numbering.
    /// <paramref name="hard"/> = true irreversibly purges every version (use it for
    /// erasure); the name then starts fresh at version 1.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="hard">Irreversibly purge every version.</param>
    /// <param name="expectedVersion">Optional precondition: fail with a <see cref="VersionConflictException"/> unless this is the current version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is invalid (no request is sent).</exception>
    public async Task<MemoryFileDeleteResult> DeleteAsync(
        string name,
        bool hard = false,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var headers = OptionalIfMatch(expectedVersion);
        var query = EntityQuery();
        if (hard)
            query.Add("hard=true");
        var (result, _) = await _client.SendMemoryFileAsync<MemoryFileDeleteResult>(
            HttpMethod.Delete, FilePath(name), query, null, headers, null, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }
}

public partial class AetherClient
{
    // ── Memory files: cross-entity raw methods ────────────────────────

    /// <summary>
    /// List current memory files across entities — e.g. every file under
    /// <c>entityPrefix: "scoped:ws_42:company:"</c>. For one entity, prefer
    /// <see cref="Memory.Files"/>.
    /// </summary>
    /// <remarks>
    /// With neither <paramref name="entityId"/> nor <paramref name="entityPrefix"/>,
    /// every memory file in scope is listed. Files are ordered by partition, entity
    /// and name. Under a multi-tenant key, call this on a partition handle.
    /// </remarks>
    /// <param name="entityId">Only this entity's files.</param>
    /// <param name="entityPrefix">Only files of entities whose id starts with this (case-sensitive).</param>
    /// <param name="prefix">Only files whose name starts with this.</param>
    /// <param name="limit">Page size, 1–1000 (server default 100).</param>
    /// <param name="cursor">A previous page's <see cref="MemoryFilePage.NextCursor"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Both <paramref name="entityId"/> and <paramref name="entityPrefix"/> are set (no request is sent).</exception>
    public async Task<MemoryFilePage> ListMemoryFilesAsync(
        string? entityId = null,
        string? entityPrefix = null,
        string? prefix = null,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var query = EntityScopeQuery(entityId, entityPrefix);
        if (!string.IsNullOrEmpty(prefix))
            query.Add("prefix=" + Uri.EscapeDataString(prefix!));
        if (limit.HasValue)
            query.Add("limit=" + limit.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(cursor))
            query.Add("cursor=" + Uri.EscapeDataString(cursor!));
        var (page, _) = await SendMemoryFileAsync<MemoryFilePage>(
            HttpMethod.Get, "/memory/files", query, null, null, null, cancellationToken).ConfigureAwait(false);
        return page;
    }

    /// <summary>
    /// Search current memory files line by line across entities (see
    /// <see cref="MemoryFiles.GrepAsync"/>). With neither <paramref name="entityId"/>
    /// nor <paramref name="entityPrefix"/>, every memory file in scope is scanned.
    /// The scan is capped (500 files, 8 MiB, 2 s, <paramref name="maxMatches"/>);
    /// a capped scan reports <see cref="MemoryGrepResult.Truncated"/>.
    /// </summary>
    /// <param name="pattern">The text or regex to find (non-empty).</param>
    /// <param name="entityId">Only this entity's files.</param>
    /// <param name="entityPrefix">Only files of entities whose id starts with this.</param>
    /// <param name="regex">Treat <paramref name="pattern"/> as a regular expression.</param>
    /// <param name="ignoreCase">Case-insensitive matching.</param>
    /// <param name="files">Only these file names.</param>
    /// <param name="context">Lines of context before and after each match, 0–10. Default 0.</param>
    /// <param name="maxMatches">Stop after this many matches, 1–1000. Default 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> is empty, or both <paramref name="entityId"/> and <paramref name="entityPrefix"/> are set (no request is sent).</exception>
    public async Task<MemoryGrepResult> GrepMemoryFilesAsync(
        string pattern,
        string? entityId = null,
        string? entityPrefix = null,
        bool regex = false,
        bool ignoreCase = false,
        IReadOnlyList<string>? files = null,
        int context = 0,
        int maxMatches = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException("pattern cannot be empty", nameof(pattern));
        RequireOneEntityScope(entityId, entityPrefix);
        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(entityId))
            body["entity_id"] = entityId;
        else if (!string.IsNullOrEmpty(entityPrefix))
            body["entity_prefix"] = entityPrefix;
        body["pattern"] = pattern;
        body["regex"] = regex;
        body["ignore_case"] = ignoreCase;
        body["context"] = context;
        body["max_matches"] = maxMatches;
        if (files != null)
            body["files"] = files;
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var (result, _) = await SendMemoryFileAsync<MemoryGrepResult>(
            HttpMethod.Post, "/memory/files/grep", new List<string>(), content, null, null, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private static void RequireOneEntityScope(string? entityId, string? entityPrefix)
    {
        if (!string.IsNullOrEmpty(entityId) && !string.IsNullOrEmpty(entityPrefix))
            throw new ArgumentException("entityId and entityPrefix are mutually exclusive", nameof(entityPrefix));
    }

    private static List<string> EntityScopeQuery(string? entityId, string? entityPrefix)
    {
        RequireOneEntityScope(entityId, entityPrefix);
        var query = new List<string>();
        if (!string.IsNullOrEmpty(entityId))
            query.Add("entity_id=" + Uri.EscapeDataString(entityId!));
        else if (!string.IsNullOrEmpty(entityPrefix))
            query.Add("entity_prefix=" + Uri.EscapeDataString(entityPrefix!));
        return query;
    }

    /// <summary>
    /// Internal transport hook for the memory-file routes: appends the client's
    /// partition to <paramref name="query"/>, sends optional precondition headers
    /// and <paramref name="idempotencyKey"/> (the same key on every retry), and
    /// returns the parsed body with the response <c>ETag</c>. Retries, timeouts and
    /// error mapping are the raw client's own.
    /// </summary>
    internal Task<(T Value, string? ETag)> SendMemoryFileAsync<T>(
        HttpMethod method,
        string path,
        List<string> query,
        HttpContent? content,
        IReadOnlyList<KeyValuePair<string, string>>? headers,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_partition))
            query.Add("partition=" + Uri.EscapeDataString(_partition!));
        var fullPath = query.Count == 0 ? path : path + "?" + string.Join("&", query);
        return RequestWithHeadersAsync<T>(
            fullPath, method, content, cancellationToken, idempotencyKey, retry: true, headers);
    }
}
