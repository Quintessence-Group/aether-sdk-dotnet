using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// The Connections API surface: mint / list /
/// get / delete / resync / browse / update selection / get purge receipt,
/// the typed exceptions, and the offline redirect-signature verifier.
/// Mirrors PartitionLifecycleTests.cs's pattern: a real client over the
/// mocked transport, so the genuine request / parse / error-mapping path
/// runs.
/// </summary>
public class ConnectionsTests
{
    private static AetherClient CreateClient(MockHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new AetherClient(http, "http://localhost:9000");
    }

    private const string ConnId = "11111111-1111-1111-1111-111111111111";

    private static object ConnectionFixture(string id) => new
    {
        connection_id = id,
        provider = "dropbox",
        owner_type = "tenant",
        owner_id = (string?)null,
        provider_account_id = "dbid:acme",
        account_display_name = (string?)null,
        target_partition = (string?)null,
        status = "active",
        granted_scopes = Array.Empty<string>(),
        created_at = "2026-08-15T00:00:00Z",
        last_sync_at = (string?)null,
        last_error = (string?)null,
        files_synced = 0,
        files_skipped = 0,
        files_deleted = 0,
        selected_paths = Array.Empty<string>(),
        purge_state = "not_started",
        purge_receipt_id = (string?)null,
        credential_deleted = false,
    };

    // ── CreateConnectSessionAsync ──────────────────────────────────

    [Fact]
    public async Task CreateConnectSession_MintsAndParsesResponse()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            session_token = "acs_deadbeef",
            connect_url = "https://connect.example.com/connect/acs_deadbeef",
            client_secret = "acsec_secretsecret",
            expires_at = "2026-08-15T00:00:00Z",
        });

        using var client = CreateClient(handler);
        var session = await client.CreateConnectSessionAsync("priya", "https://acme.example.com/cb");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/v1/connections/sessions", handler.LastRequest.RequestUri!.AbsolutePath);
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("dropbox", body.GetProperty("provider").GetString());
        Assert.Equal("priya", body.GetProperty("external_user_id").GetString());
        Assert.Equal("acs_deadbeef", session.SessionToken);
        Assert.Equal("acsec_secretsecret", session.ClientSecret);
    }

    [Fact]
    public async Task CreateConnectSession_OnAHandle_AssertsPartition()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            session_token = "acs_x",
            connect_url = "https://connect.example.com/connect/acs_x",
            client_secret = "acsec_x",
            expires_at = "2026-08-15T00:00:00Z",
        });

        using var client = CreateClient(handler);
        await client.Partition("priya").CreateConnectSessionAsync("priya", "https://acme.example.com/cb");

        Assert.Contains("partition=priya", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task CreateConnectSession_RejectsEmptyArgs()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateConnectSessionAsync("", "https://x.example.com"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateConnectSessionAsync("priya", ""));
    }

    [Fact]
    public async Task CreateConnectSession_PartitionMismatch_IsTyped()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "partition mismatch", code = "partition_mismatch" },
            HttpStatusCode.BadRequest);
        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<PartitionMismatchException>(
            () => client.Partition("someone-else").CreateConnectSessionAsync("priya", "https://acme.example.com/cb"));
        Assert.Equal("partition_mismatch", ex.ErrorCode);
    }

    [Fact]
    public async Task CreateConnectSession_SessionInvalid_IsTyped()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "already used", code = "session_invalid" },
            HttpStatusCode.BadRequest);
        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<SessionInvalidException>(
            () => client.CreateConnectSessionAsync("priya", "https://acme.example.com/cb"));
        Assert.Equal("session_invalid", ex.ErrorCode);
    }

    // ── ListConnectionsAsync ───────────────────────────────────────

    [Fact]
    public async Task ListConnections_ParsesEveryField()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            connections = new[]
            {
                new
                {
                    connection_id = ConnId,
                    provider = "dropbox",
                    owner_type = "external_user",
                    owner_id = "priya",
                    provider_account_id = "dbid:priya",
                    account_display_name = "Priya",
                    target_partition = "priya",
                    status = "active",
                    granted_scopes = new[] { "files.metadata.read" },
                    created_at = "2026-08-15T00:00:00Z",
                    last_sync_at = (string?)null,
                    last_error = (string?)null,
                    files_synced = 3,
                    files_skipped = 0,
                    files_deleted = 0,
                    selected_paths = Array.Empty<string>(),
                    purge_state = "not_started",
                    purge_receipt_id = (string?)null,
                    credential_deleted = false,
                },
            },
        });

        using var client = CreateClient(handler);
        var conns = await client.ListConnectionsAsync();

        Assert.Equal("/v1/connections", handler.LastRequest!.RequestUri!.AbsolutePath);
        var c = Assert.Single(conns);
        Assert.Equal("external_user", c.OwnerType);
        Assert.Equal("priya", c.OwnerId);
        Assert.Equal("priya", c.TargetPartition);
        Assert.Equal(3, c.FilesSynced);
    }

    [Fact]
    public async Task ListConnections_SendsOwnerFiltersAndIncludePurged()
    {
        var handler = MockHttpMessageHandler.WithJson(new { connections = Array.Empty<object>() });
        using var client = CreateClient(handler);
        await client.ListConnectionsAsync(new ListConnectionsOptions
        {
            OwnerType = "external_user",
            OwnerId = "priya",
            IncludePurged = false,
        });

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("owner_type=external_user", query);
        Assert.Contains("owner_id=priya", query);
        Assert.Contains("include_purged=false", query);
    }

    // ── GetConnectionAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetConnection_FetchesById()
    {
        var handler = MockHttpMessageHandler.WithJson(ConnectionFixture(ConnId));
        using var client = CreateClient(handler);
        var conn = await client.GetConnectionAsync(ConnId);

        Assert.Equal($"/v1/connections/{ConnId}", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(ConnId, conn.ConnectionId);
        Assert.Null(conn.TargetPartition);
    }

    [Fact]
    public async Task GetConnection_WrongPartition_IsThePlainNotFound()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "unknown connection", code = "connection_not_found" },
            HttpStatusCode.NotFound);
        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => client.Partition("someone-else").GetConnectionAsync(ConnId));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("connection_not_found", ex.ErrorCode);
    }

    [Fact]
    public async Task GetConnection_RejectsEmptyId()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetConnectionAsync(""));
    }

    // ── DeleteConnectionAsync ──────────────────────────────────────

    [Fact]
    public async Task DeleteConnection_ParsesPurgeSummary()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            connection_id = ConnId,
            status = "revoked",
            purge = new
            {
                receipt_id = "r1",
                documents_purged = 5,
                merkle_root = "deadbeef",
                completed_at = "2026-08-15T00:00:00Z",
                signer_node_id = "node-1",
            },
        });

        using var client = CreateClient(handler);
        var result = await client.DeleteConnectionAsync(ConnId);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("revoked", result.Status);
        Assert.NotNull(result.Purge);
        Assert.Equal(5, result.Purge!.DocumentsPurged);
    }

    [Fact]
    public async Task DeleteConnection_IdempotentNoOp_HasNoPurge()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            connection_id = ConnId,
            status = "revoked",
            purge = (object?)null,
        });

        using var client = CreateClient(handler);
        var result = await client.DeleteConnectionAsync(ConnId);

        Assert.Null(result.Purge);
    }

    [Fact]
    public async Task DeleteConnection_RejectsEmptyId()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteConnectionAsync(""));
    }

    // ── ResyncConnectionAsync ──────────────────────────────────────

    [Fact]
    public async Task ResyncConnection_PostsThenRefetches()
    {
        var calls = new List<string>();
        var handler = new MockHttpMessageHandler(req =>
        {
            calls.Add($"{req.Method} {req.RequestUri!.AbsolutePath}");
            if (req.RequestUri!.AbsolutePath == $"/v1/connections/{ConnId}/resync")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { connection_id = ConnId, status = "active" }),
                        Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(ConnectionFixture(ConnId)), Encoding.UTF8, "application/json"),
            };
        });

        using var client = CreateClient(handler);
        var conn = await client.ResyncConnectionAsync(ConnId);

        Assert.Equal("active", conn.Status);
        Assert.Contains($"POST /v1/connections/{ConnId}/resync", calls);
        Assert.Contains($"GET /v1/connections/{ConnId}", calls);
    }

    // ── BrowseConnectionAsync / UpdateSelectionAsync ───────────────

    [Fact]
    public async Task BrowseConnection_SendsPathAndCursor_ParsesPage()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            entries = new[]
            {
                new { name = "q3.txt", path_display = "/Reports/q3.txt", is_folder = false, size_bytes = (long?)42 },
            },
            next_cursor = "cursor-2",
        });

        using var client = CreateClient(handler);
        var page = await client.BrowseConnectionAsync(ConnId, "/Reports");

        Assert.Equal($"/v1/connections/{ConnId}/browse", handler.LastRequest!.RequestUri!.AbsolutePath);
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        Assert.Equal("/Reports", body.GetProperty("path").GetString());
        var entry = Assert.Single(page.Entries);
        Assert.Equal("q3.txt", entry.Name);
        Assert.Equal("cursor-2", page.NextCursor);
    }

    [Fact]
    public async Task UpdateSelection_ReplacesPaths_ReturnsNormalized()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            connection_id = ConnId,
            selected_paths = new[] { "/Reports" },
        });

        using var client = CreateClient(handler);
        var paths = await client.UpdateSelectionAsync(ConnId, new[] { "/Reports" });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal(new[] { "/Reports" }, paths);
    }

    // ── GetPurgeReceiptAsync ────────────────────────────────────────

    [Fact]
    public async Task GetPurgeReceipt_ParsesFullShape()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            version = "1",
            receipt_id = "r1",
            tenant_id = "t1",
            connection_id = "c1",
            provider = "dropbox",
            owner = "external_user:priya",
            provider_account_id = "dbid:priya",
            documents_purged = 5,
            documents_failed = 0,
            merkle_root = "deadbeef",
            merkle_leaf_count = 5,
            purged_document_ids = new[] { "d1", "d2" },
            partitions_touched = new[] { "priya" },
            default_partition_touched = false,
            credential_revocation = "revoked",
            credential_deleted = true,
            started_at = "2026-08-15T00:00:00Z",
            completed_at = "2026-08-15T00:00:01Z",
            signer_node_id = "node-1",
            signer_public_key = "pub-1",
            signature = "sig-1",
            verified = true,
        });

        using var client = CreateClient(handler);
        var receipt = await client.GetPurgeReceiptAsync("r1");

        Assert.Equal(5, receipt.DocumentsPurged);
        Assert.True(receipt.Verified);
    }

    // ── AetherConnections.VerifyRedirectSignature (pure, offline) ──

    private static string ReferenceSig(string clientSecret, string session, string status, string connectionId)
    {
        byte[] key;
        using (var sha256 = SHA256.Create())
        {
            key = sha256.ComputeHash(Encoding.UTF8.GetBytes(clientSecret));
        }
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{session}|{status}|{connectionId}"));
        var sb = new StringBuilder();
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    [Fact]
    public void VerifyRedirectSignature_AcceptsACorrectSignature()
    {
        var secret = "acsec_the-real-secret";
        var sig = ReferenceSig(secret, "acs_tok", "active", "conn-1");
        Assert.True(AetherConnections.VerifyRedirectSignature(secret, "acs_tok", "active", "conn-1", sig));
    }

    [Fact]
    public void VerifyRedirectSignature_RejectsATamperedParam()
    {
        var secret = "acsec_the-real-secret";
        var sig = ReferenceSig(secret, "acs_tok", "active", "conn-1");
        Assert.False(AetherConnections.VerifyRedirectSignature(secret, "acs_tok", "error", "conn-1", sig));
    }

    [Fact]
    public void VerifyRedirectSignature_RejectsTheWrongSecret()
    {
        var sig = ReferenceSig("acsec_the-real-secret", "acs_tok", "active", "conn-1");
        Assert.False(AetherConnections.VerifyRedirectSignature(
            "acsec_a-different-secret", "acs_tok", "active", "conn-1", sig));
    }
}
