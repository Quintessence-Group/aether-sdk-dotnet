using System.Net;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

public class ResponseLifetimeTests
{
    private sealed class TrackingContent(string body) : StringContent(body)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class Handler(HttpStatusCode status, TrackingContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    private static async Task CallAsync(AetherClient client, string operation)
    {
        switch (operation)
        {
            case "json":
                await client.GetAsync("document");
                break;
            case "raw":
                await client.DownloadAsync("document");
                break;
            case "void":
                await client.DeleteAsync("document");
                break;
            case "stream":
                using (var input = new MemoryStream(new byte[] { 1, 2, 3 }))
                    await client.InsertStreamAsync(input);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("raw", false)]
    [InlineData("void", false)]
    [InlineData("stream", false)]
    [InlineData("json", true)]
    [InlineData("raw", true)]
    [InlineData("void", true)]
    [InlineData("stream", true)]
    public async Task ReleasesResponseContentOnSuccessAndMalformedApiError(string operation, bool error)
    {
        using var content = new TrackingContent(error ? "not JSON" : "{}");
        using var http = new HttpClient(new Handler(error ? HttpStatusCode.BadRequest : HttpStatusCode.OK, content));
        using var client = new AetherClient(http, "http://localhost:9000");
        if (error)
        {
            var ex = await Assert.ThrowsAsync<AetherApiException>(() => CallAsync(client, operation));
            Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
            Assert.Equal("Bad Request", ex.Message);
        }
        else
        {
            await CallAsync(client, operation);
        }
        Assert.True(content.IsDisposed, "A completed request must release its response content.");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("stream")]
    public async Task ReleasesResponseWhenSuccessfulBodyCannotBeDeserialized(string operation)
    {
        using var content = new TrackingContent("not JSON");
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, content));
        using var client = new AetherClient(http, "http://localhost:9000");
        await Assert.ThrowsAsync<JsonException>(() => CallAsync(client, operation));
        Assert.True(content.IsDisposed, "Deserialization failures must release the response too.");
    }
}
