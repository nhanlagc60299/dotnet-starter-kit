using System.Text;
using System.Text.Json;
using FSH.Modules.Auditing.Contracts;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Auditing;

/// <summary>
/// Regression: the HTTP audit middleware parses JSON request bodies to mask them. A repeated key
/// (<c>{"a":1,"a":2}</c>) made the parse throw before the request reached routing, so EVERY endpoint
/// returned 500. A body that can't be parsed can't be masked either, so the audit row must carry a
/// marker instead of the raw text — otherwise a repeated <c>password</c> would be logged in clear.
///
/// Each test posts to a unique, unrouted probe path: the middleware runs before routing (the request
/// just 404s), and the unique path lets <c>?search=</c> find exactly this request's audit row.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class AuditRequestBodyCaptureTests
{
    private const string UnparsableMarker = "[omitted: body could not be parsed for masking]";

    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public AuditRequestBodyCaptureTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"name":"x","password":"s3cret-dup-1","password":"s3cret-dup-2"}""")]
    [InlineData("""{"password":"s3cret-trunc""")]
    public async Task UnparsableBody_Should_NotFailRequest_And_NotBeAuditedRaw(string body)
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        var (status, preview) = await PostAndReadRequestPreviewAsync(client, body);

        status.ShouldNotBe(HttpStatusCode.InternalServerError);
        preview.ValueKind.ShouldBe(JsonValueKind.String);
        preview.GetString().ShouldBe(UnparsableMarker);
    }

    [Fact]
    public async Task DuplicatePasswordKey_Should_NeverReachAuditLog()
    {
        var secret1 = $"dup-secret-{Guid.NewGuid():N}";
        var secret2 = $"dup-secret-{Guid.NewGuid():N}";
        using var client = await _auth.CreateRootAdminClientAsync();

        var (status, preview) = await PostAndReadRequestPreviewAsync(
            client, $$"""{"password":"{{secret1}}","password":"{{secret2}}"}""");

        status.ShouldNotBe(HttpStatusCode.InternalServerError);
        var raw = preview.GetRawText();
        raw.ShouldNotContain(secret1);
        raw.ShouldNotContain(secret2);
    }

    [Fact]
    public async Task DuplicatePasswordKey_Should_LetEndpointDecide_When_PostedToTokenEndpoint()
    {
        using var anon = _factory.CreateClient();
        anon.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        var response = await anon.PostAsync(
            $"{TestConstants.IdentityBasePath}/token/issue",
            Json($$"""{"email":"{{TestConstants.RootAdminEmail}}","password":"wrong","password":"{{TestConstants.DefaultPassword}}"}"""));

        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task NormalBody_Should_StillBeAuditedWithSensitiveFieldsMasked()
    {
        var secret = $"plain-secret-{Guid.NewGuid():N}";
        using var client = await _auth.CreateRootAdminClientAsync();

        var (status, preview) = await PostAndReadRequestPreviewAsync(
            client, $$"""{"name":"visible-name","password":"{{secret}}"}""");

        status.ShouldNotBe(HttpStatusCode.InternalServerError);
        preview.ValueKind.ShouldBe(JsonValueKind.Object);
        preview.GetProperty("name").GetString().ShouldBe("visible-name");
        preview.GetProperty("password").GetString().ShouldBe("****");
        preview.GetRawText().ShouldNotContain(secret);
    }

    private static async Task<(HttpStatusCode Status, JsonElement RequestPreview)> PostAndReadRequestPreviewAsync(
        HttpClient client, string body)
    {
        var probe = $"audit-body-probe-{Guid.NewGuid():N}";
        using var response = await client.PostAsync($"/api/v1/{probe}", Json(body));

        Guid? id = null;
        for (int i = 0; i < AuditTestHelper.DefaultPollAttempts && id is null; i++)
        {
            var page = await AuditTestHelper.GetAuditsPageAsync(client, pageSize: 10, extraQuery: $"search={probe}");
            id = page.Items.FirstOrDefault(a => a.EventType == AuditEventType.Activity)?.Id;
            if (id is null)
            {
                await Task.Delay(AuditTestHelper.PollInterval);
            }
        }

        id.ShouldNotBeNull($"no activity audit row was written for {probe}");
        var detail = await AuditTestHelper.GetByIdAsync(client, id.Value);
        detail.ShouldNotBeNull();
        return (response.StatusCode, detail.Payload.GetProperty("requestPreview"));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
