using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Relay.Tests;

public class RelayTests
{
    const string Secret = "test-secret";
    const string AdminKey = "test-admin";

    // Downstream stub: answers with the queued status codes in order, then 200.
    class Downstream : HttpMessageHandler
    {
        public readonly Queue<HttpStatusCode> Responses = new();
        public readonly List<(string Body, string? Key)> Received = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Received.Add((await req.Content!.ReadAsStringAsync(ct),
                req.Headers.TryGetValues("Idempotency-Key", out var v) ? v.Single() : null));
            return new HttpResponseMessage(Responses.Count > 0 ? Responses.Dequeue() : HttpStatusCode.OK);
        }
    }

    static (HttpClient client, Downstream downstream) Start()
    {
        var downstream = new Downstream();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Relay:Secret", Secret);
            b.UseSetting("Relay:AdminKey", AdminKey);
            b.UseSetting("Relay:TargetUrl", "http://downstream.test/hook");
            b.UseSetting("Relay:MaxAttempts", "3");
            b.UseSetting("Relay:BaseDelayMs", "1");
            b.ConfigureTestServices(s =>
                s.AddHttpClient("downstream").ConfigurePrimaryHttpMessageHandler(() => downstream));
        });
        return (factory.CreateClient(), downstream);
    }

    static HttpRequestMessage Signed(string json, string secret = Secret)
    {
        var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Signature", "sha256=" + sig);
        return req;
    }

    static async Task<string> Status(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;

    [Fact]
    public async Task Valid_event_is_forwarded_with_idempotency_key()
    {
        var (client, downstream) = Start();
        var json = """{"id":"evt_1","total":42}""";

        var res = await client.SendAsync(Signed(json));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("delivered", await Status(res));
        Assert.Equal((json, "evt_1"), Assert.Single(downstream.Received));
    }

    [Fact]
    public async Task Wrong_or_missing_signature_is_rejected_and_not_forwarded()
    {
        var (client, downstream) = Start();

        var wrongSecret = await client.SendAsync(Signed("""{"id":"evt_2"}""", "other-secret"));
        var noHeader = await client.PostAsync("/webhooks", new StringContent("""{"id":"evt_2"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noHeader.StatusCode);
        Assert.Empty(downstream.Received);
    }

    [Fact]
    public async Task Body_without_id_is_rejected()
    {
        var (client, downstream) = Start();

        var res = await client.SendAsync(Signed("""{"total":42}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(downstream.Received);
    }

    [Fact]
    public async Task Duplicate_event_is_forwarded_only_once()
    {
        var (client, downstream) = Start();
        var json = """{"id":"evt_3"}""";

        await client.SendAsync(Signed(json));
        var second = await client.SendAsync(Signed(json));

        Assert.Equal("duplicate", await Status(second));
        Assert.Single(downstream.Received);
    }

    [Fact]
    public async Task Transient_downstream_errors_are_retried_until_success()
    {
        var (client, downstream) = Start();
        downstream.Responses.Enqueue(HttpStatusCode.ServiceUnavailable);
        downstream.Responses.Enqueue(HttpStatusCode.TooManyRequests);

        var res = await client.SendAsync(Signed("""{"id":"evt_4"}"""));

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("delivered", body.GetProperty("status").GetString());
        Assert.Equal(3, body.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task Permanent_client_error_is_not_retried()
    {
        var (client, downstream) = Start();
        downstream.Responses.Enqueue(HttpStatusCode.BadRequest);

        var res = await client.SendAsync(Signed("""{"id":"evt_5"}"""));

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("dead-lettered", await Status(res));
        Assert.Single(downstream.Received);
    }

    [Fact]
    public async Task Exhausted_retries_go_to_dead_letters_and_replay_delivers()
    {
        var (client, downstream) = Start();
        for (var i = 0; i < 3; i++) downstream.Responses.Enqueue(HttpStatusCode.InternalServerError);

        var res = await client.SendAsync(Signed("""{"id":"evt_6"}"""));
        Assert.Equal("dead-lettered", await Status(res));
        Assert.Equal(3, downstream.Received.Count);

        var list = await Admin(client, HttpMethod.Get, "/dead-letters");
        var item = Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("evt_6", item.GetProperty("id").GetString());
        Assert.Equal("HTTP 500", item.GetProperty("lastError").GetString());

        var replay = await Admin(client, HttpMethod.Post, "/dead-letters/evt_6/replay");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(4, downstream.Received.Count);

        var after = await Admin(client, HttpMethod.Get, "/dead-letters");
        Assert.Empty((await after.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Admin_endpoints_require_admin_key()
    {
        var (client, _) = Start();

        var noKey = await client.GetAsync("/dead-letters");
        var unknown = await Admin(client, HttpMethod.Post, "/dead-letters/nope/replay");

        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    static Task<HttpResponseMessage> Admin(HttpClient client, HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Admin-Key", AdminKey);
        return client.SendAsync(req);
    }
}
