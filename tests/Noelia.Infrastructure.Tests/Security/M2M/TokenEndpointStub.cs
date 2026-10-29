using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Noelia.Infrastructure.Tests.Security.M2M;

/// <summary>
/// A token endpoint that counts what it was asked and answers as the real one does
/// (<c>access_token</c>, <c>token_type</c>, <c>expires_in</c>), or as configured to fail.
/// </summary>
internal sealed class TokenEndpointStub : HttpMessageHandler
{
    private int _requests;
    private int _issued;

    public ConcurrentQueue<(string ContentType, Dictionary<string, string> Form)> Seen { get; } = new();

    public ConcurrentQueue<string> Bodies { get; } = new();

    public int Requests => Volatile.Read(ref _requests);

    public int ExpiresIn { get; set; } = 300;

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string? RawBody { get; set; }

    public Exception? Throw { get; set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>The token the n-th request received; tokens are told apart by their counter.</summary>
    public static string TokenNumber(int n) => "token-" + n;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Bodies.Enqueue(body);
        var form = new Dictionary<string, string>();
        foreach (var pair in QueryHelpers.ParseQuery(body))
        {
            form[pair.Key] = pair.Value.ToString();
        }

        Seen.Enqueue((request.Content?.Headers.ContentType?.MediaType ?? "", form));

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        if (Status != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent("{\"error\":\"invalid_client\"}", Encoding.UTF8, "application/json")
            };
        }

        var json = RawBody ?? JsonSerializer.Serialize(new
        {
            access_token = TokenNumber(Interlocked.Increment(ref _issued)),
            token_type = "Bearer",
            expires_in = ExpiresIn
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
