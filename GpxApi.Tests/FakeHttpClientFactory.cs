namespace GpxApi.Tests;

/// <summary>
/// Fabryka HttpClient z podstawioną odpowiedzią - do testów bez sieci.
/// </summary>
public class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public int RequestCount { get; private set; }

    public FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public HttpClient CreateClient(string name) => new(new FakeHandler(this));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly FakeHttpClientFactory _owner;

        public FakeHandler(FakeHttpClientFactory owner) => _owner = owner;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _owner.RequestCount++;
            return Task.FromResult(_owner._responder(request));
        }
    }
}
