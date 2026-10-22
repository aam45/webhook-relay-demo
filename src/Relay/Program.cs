using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("downstream");
var app = builder.Build();

var cfg = app.Configuration.GetSection("Relay");
var secret = Encoding.UTF8.GetBytes(cfg["Secret"] ?? throw new InvalidOperationException("Relay:Secret missing"));
var adminKey = cfg["AdminKey"] ?? throw new InvalidOperationException("Relay:AdminKey missing");
var targetUrl = cfg["TargetUrl"] ?? throw new InvalidOperationException("Relay:TargetUrl missing");
var maxAttempts = cfg.GetValue("MaxAttempts", 4);
var baseDelay = TimeSpan.FromMilliseconds(cfg.GetValue("BaseDelayMs", 500));

// In-memory state, lost on restart. Use a DB table with a unique event id in production.
var seen = new ConcurrentDictionary<string, byte>();
var deadLetters = new ConcurrentDictionary<string, DeadLetter>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/webhooks", async (HttpRequest req, IHttpClientFactory http) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var body = ms.ToArray();

    if (!SignatureValid(body, req.Headers["X-Signature"].ToString()))
        return Results.Unauthorized();

    string? id;
    try { using var doc = JsonDocument.Parse(body); id = doc.RootElement.GetProperty("id").GetString(); }
    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { id = null; }
    if (string.IsNullOrWhiteSpace(id))
        return Results.BadRequest(new { error = "body must be JSON with a string field 'id'" });

    if (!seen.TryAdd(id, 0))
        return Results.Ok(new { id, status = "duplicate" });

    var (ok, attempts, error) = await Forward(http, id, body);
    if (ok) return Results.Ok(new { id, status = "delivered", attempts });

    deadLetters[id] = new DeadLetter(id, Encoding.UTF8.GetString(body), attempts, error, DateTimeOffset.UtcNow);
    return Results.Accepted($"/dead-letters/{id}", new { id, status = "dead-lettered", attempts, error });
});

var admin = app.MapGroup("/dead-letters").AddEndpointFilter(async (ctx, next) =>
    FixedEquals(ctx.HttpContext.Request.Headers["X-Admin-Key"].ToString(), adminKey)
        ? await next(ctx)
        : Results.Unauthorized());

admin.MapGet("/", () => Results.Ok(deadLetters.Values.OrderBy(d => d.FailedAt)));

admin.MapPost("/{id}/replay", async (string id, IHttpClientFactory http) =>
{
    if (!deadLetters.TryGetValue(id, out var dl)) return Results.NotFound();
    var (ok, attempts, error) = await Forward(http, id, Encoding.UTF8.GetBytes(dl.Body));
    if (!ok)
    {
        deadLetters[id] = dl with { Attempts = dl.Attempts + attempts, LastError = error, FailedAt = DateTimeOffset.UtcNow };
        return Results.Json(new { id, status = "still-failing", attempts, error }, statusCode: 502);
    }
    deadLetters.TryRemove(id, out _);
    return Results.Ok(new { id, status = "delivered", attempts });
});

app.Run();

bool SignatureValid(byte[] body, string header)
{
    const string prefix = "sha256=";
    if (!header.StartsWith(prefix)) return false;
    var expected = Convert.ToHexString(HMACSHA256.HashData(secret, body)).ToLowerInvariant();
    return FixedEquals(header[prefix.Length..].ToLowerInvariant(), expected);
}

static bool FixedEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

async Task<(bool ok, int attempts, string? error)> Forward(IHttpClientFactory http, string id, byte[] body)
{
    var client = http.CreateClient("downstream");
    string? error = null;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, targetUrl)
            {
                Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } }
            };
            msg.Headers.Add("Idempotency-Key", id);
            using var res = await client.SendAsync(msg);
            if (res.IsSuccessStatusCode) return (true, attempt, null);
            error = $"HTTP {(int)res.StatusCode}";
            // 4xx other than 408/429 will not fix itself: stop retrying.
            var code = (int)res.StatusCode;
            if (code is >= 400 and < 500 and not 408 and not 429) return (false, attempt, error);
        }
        catch (HttpRequestException e) { error = e.Message; }
        catch (TaskCanceledException) { error = "timeout"; }

        if (attempt < maxAttempts)
            await Task.Delay(baseDelay * Math.Pow(2, attempt - 1));
    }
    return (false, maxAttempts, error);
}

record DeadLetter(string Id, string Body, int Attempts, string? LastError, DateTimeOffset FailedAt);

public partial class Program { }
