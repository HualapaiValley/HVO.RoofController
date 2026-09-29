using System.Net;
using System.Net.Http.Json;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// Sends REST requests with the current credential's headers and turns error answers into <see cref="RoofApiException"/>.
/// A request refused with 401 is retried once when the credential changed in response (a kiosk dropping an expired PIN
/// session), but only for reads: a refused command is reported, never repeated under another identity.
/// </summary>
internal sealed class RoofHttp : IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<RoofCredential?> _credential;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;

    public RoofHttp(RoofConnectionOptions options, Func<RoofCredential?> credential, TimeSpan timeout)
    {
        _client = new HttpClient(options.CreatePrimaryHandler(), disposeHandler: true)
        {
            BaseAddress = options.NormalizedBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
        _credential = credential;
        _timeout = timeout;
        _time = options.TimeProvider;
    }

    public Uri BaseAddress => _client.BaseAddress!;

    /// <summary>
    /// Sends a request, with no credential when <paramref name="use"/> is null, and returns the response when its status
    /// is a success or <paramref name="alsoAccept"/>; otherwise throws <see cref="RoofApiException"/>. The timeout covers
    /// the request until its headers arrive, so a stream read with <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// may run on.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        RoofCredentialUse? use,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        HttpStatusCode? alsoAccept = null,
        TimeSpan? timeout = null)
    {
        var retried = false;
        while (true)
        {
            using var request = new HttpRequestMessage(method, path);
            var credential = use is null ? null : _credential();
            if (credential is not null)
            {
                foreach (var header in credential.GetHeaders(use!.Value))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: RoofClientJson.Options);
            }

            var response = await SendWithTimeoutAsync(request, completion, timeout ?? _timeout, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == alsoAccept)
            {
                return response;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && credential is not null)
                {
                    var changed = credential.OnRefused(use!.Value);
                    if (changed && !retried && method == HttpMethod.Get)
                    {
                        retried = true;
                        continue;
                    }
                }

                throw await RoofApiException.FromResponseAsync(response, _time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken, RoofCredentialUse? use = RoofCredentialUse.Request)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, use, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, RoofCredentialUse? use = RoofCredentialUse.Request)
    {
        using var response = await SendAsync(method, path, body, use, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken, RoofCredentialUse? use = RoofCredentialUse.Request)
    {
        using var response = await SendAsync(method, path, body, use, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(RoofClientJson.Options, cancellationToken).ConfigureAwait(false);
            return value ?? throw new RoofProtocolException($"The controller sent an empty answer to {Describe(response)}.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new RoofProtocolException($"The controller's answer to {Describe(response)} could not be read.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(
        HttpRequestMessage request,
        HttpCompletionOption completion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            return await _client.SendAsync(request, completion, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The controller did not answer {request.Method} {request.RequestUri} within {timeout.TotalSeconds:0.#} s.", ex);
        }
    }

    private static string Describe(HttpResponseMessage response)
        => response.RequestMessage is { } request ? $"{request.Method} {request.RequestUri?.AbsolutePath}" : "a request";

    public void Dispose() => _client.Dispose();
}

/// <summary>The controller answered with something this client cannot read: an empty or malformed body.</summary>
public sealed class RoofProtocolException : Exception
{
    public RoofProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
