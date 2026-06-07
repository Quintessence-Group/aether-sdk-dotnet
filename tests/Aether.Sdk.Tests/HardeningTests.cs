using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>Records every request it sees, so retry behavior can be asserted.</summary>
internal class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<HttpStatusCode> _statuses;
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Body returned for every response. Defaults to a DocumentRecord shape.</summary>
    public object ResponseBody { get; set; } =
        new { doc_id = "d1", cid = "c1", chunks = 1, vectors = 1, version = 1 };

    public RecordingHandler(params HttpStatusCode[] statuses)
    {
        _statuses = new Queue<HttpStatusCode>(statuses);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var status = _statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek();
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(ResponseBody),
                Encoding.UTF8, "application/json"),
        });
    }
}

public class HardeningTests
{
    private static readonly Regex UuidV4 = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase);

    [Fact]
    public async Task SendsVersionedUserAgent()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var client = new AetherClient(new HttpClient(handler), "http://localhost:9000");
        await client.InsertTextAsync("hello");
        var ua = handler.Requests[0].Headers.UserAgent.ToString();
        Assert.StartsWith("aether-sdk-dotnet/", ua);
    }

    [Fact]
    public async Task PostCarriesIdempotencyKey()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var client = new AetherClient(new HttpClient(handler), "http://localhost:9000");
        await client.InsertTextAsync("hello");
        Assert.True(handler.Requests[0].Headers.TryGetValues("Idempotency-Key", out var values));
        Assert.Matches(UuidV4, values!.Single());
    }

    [Fact]
    public async Task IdempotencyKeyStableAcrossRetries()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var client = new AetherClient(new HttpClient(handler), "http://localhost:9000");
        await client.InsertTextAsync("hello");
        Assert.Equal(2, handler.Requests.Count);
        var k0 = handler.Requests[0].Headers.GetValues("Idempotency-Key").Single();
        var k1 = handler.Requests[1].Headers.GetValues("Idempotency-Key").Single();
        Assert.Equal(k0, k1);
    }

    [Fact]
    public async Task GetHasNoIdempotencyKey()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK)
        {
            ResponseBody = new { node_id = 0, documents = 0, vectors = 0 },
        };
        var client = new AetherClient(new HttpClient(handler), "http://localhost:9000");
        await client.StatusAsync();
        Assert.False(handler.Requests[0].Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public void HttpWithKeyToRemoteHostThrows()
    {
        Assert.Throws<AetherException>(() =>
            new AetherClient(new AetherClientOptions { BaseUrl = "http://api.aetherdb.ai", ApiKey = "secret" }));
    }

    [Theory]
    [InlineData("http://localhost:9000")]
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("https://api.aetherdb.ai")]
    public void AllowedConfigurationsDoNotThrow(string baseUrl)
    {
        using var client = new AetherClient(new AetherClientOptions { BaseUrl = baseUrl, ApiKey = "secret" });
    }

    [Fact]
    public void HttpWithoutKeyAllowed()
    {
        using var client = new AetherClient(new AetherClientOptions { BaseUrl = "http://api.aetherdb.ai" });
    }
}
