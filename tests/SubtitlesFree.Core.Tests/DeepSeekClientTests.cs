using System.Net;
using SubtitlesFree.Core;
using Xunit;

namespace SubtitlesFree.Core.Tests;

public class DeepSeekClientTests
{
    /// <summary>用假 HttpMessageHandler 模拟 /models 响应，不打真网络。</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ListModels_ParsesIds_SortedDistinct()
    {
        var handler = new FakeHandler(_ => Json(
            """{"object":"list","data":[{"id":"deepseek-v4-pro"},{"id":"deepseek-flash"},{"id":"deepseek-v4-pro"}]}"""));
        string[]? models = await DeepSeekClient.ListModels("sk-1", handler);
        Assert.NotNull(models);
        Assert.Equal(["deepseek-flash", "deepseek-v4-pro"], models);
    }

    [Fact]
    public async Task ListModels_SendsBearerAuth()
    {
        string? seen = null;
        var handler = new FakeHandler(r =>
        {
            seen = r.Headers.Authorization?.Scheme;
            return Json("""{"data":[]}""");
        });
        await DeepSeekClient.ListModels("sk-secret", handler);
        Assert.Equal("Bearer", seen);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    public async Task ListModels_NonSuccess_ReturnsNull(int code)
    {
        var handler = new FakeHandler(_ => Json("{}", (HttpStatusCode)code));
        Assert.Null(await DeepSeekClient.ListModels("k", handler));
    }

    [Fact]
    public async Task ListModels_MalformedJson_ReturnsNull()
    {
        var handler = new FakeHandler(_ => Json("<html>gateway error</html>"));
        Assert.Null(await DeepSeekClient.ListModels("k", handler));
    }

    [Fact]
    public async Task ListModels_HandlerThrows_ReturnsNull()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("conn refused"));
        Assert.Null(await DeepSeekClient.ListModels("k", handler));
    }
}
