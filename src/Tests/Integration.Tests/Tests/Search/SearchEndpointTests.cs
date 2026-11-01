using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Search;

/// <summary>
/// End-to-end Vietnamese full-text search over a real PostgreSQL (unaccent + the search.vi configuration).
/// Each test indexes under its own EntityType and filters on it, so documents from other tests in the shared
/// database never affect the assertions.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class SearchEndpointTests
{
    private readonly AuthHelper _auth;

    public SearchEndpointTests(FshWebApplicationFactory factory)
    {
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task Search_Should_FindAccentedTitle_When_QueryHasNoAccents()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        string type = UniqueType();
        await IndexAsync(client, type, "1", "Học sinh giỏi", summary: "Danh sách học sinh giỏi năm nay");

        var hits = await SearchAsync(client, "hoc sinh", type);

        var hit = hits.ShouldHaveSingleItem();
        hit.EntityId.ShouldBe("1");
        hit.Title.ShouldBe("Học sinh giỏi");
        hit.Snippet.ShouldNotBeNull();
        hit.Snippet.ShouldContain("<mark>");
    }

    [Fact]
    public async Task Search_Should_RankPhraseMatchFirst_When_WordsAppearInBothOrders()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        string type = UniqueType();
        // Biology is indexed last, so it would win the UpdatedAtUtc tie-break; only the phrase boost puts the student first.
        await IndexAsync(client, type, "student", "Học sinh tiêu biểu");
        await IndexAsync(client, type, "bio", "Sinh học phân tử");

        var hits = await SearchAsync(client, "học sinh", type);

        hits.Select(h => h.EntityId).ShouldBe(["student", "bio"]);
        hits[0].Rank.ShouldBeGreaterThan(hits[1].Rank);
    }

    [Theory]
    [InlineData("Du lịch Đà Nẵng", "da nang")]
    [InlineData("Ve may bay da nang", "Đà Nẵng")]
    [InlineData("Lớp12A chuyên toán", "lop12a")]
    [InlineData("Phòng301 tầng hai", "phong301")]
    [InlineData("Lớp-chuyên2 toán", "lop chuyen2")]
    public async Task Search_Should_MatchAcrossAccents_InBothDirections(string title, string query)
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        string type = UniqueType();
        await IndexAsync(client, type, "1", title);

        var hits = await SearchAsync(client, query, type);

        hits.ShouldHaveSingleItem().Title.ShouldBe(title);
    }

    [Fact]
    public async Task Index_Should_ReplaceExistingEntry_When_SameEntityIndexedAgain()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        string type = UniqueType();
        await IndexAsync(client, type, "1", "Bản nháp cũ");
        await IndexAsync(client, type, "1", "Bài viết mới");

        (await SearchAsync(client, "nhap", type)).ShouldBeEmpty();
        (await SearchAsync(client, "bai viet", type)).ShouldHaveSingleItem().Title.ShouldBe("Bài viết mới");
    }

    [Fact]
    public async Task Remove_Should_DropEntryFromResults_And_BeIdempotent()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        string type = UniqueType();
        await IndexAsync(client, type, "1", "Hợp đồng lao động");

        using (var removed = await client.DeleteAsync($"{TestConstants.SearchBasePath}/documents/{type}/1"))
        {
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        using (var again = await client.DeleteAsync($"{TestConstants.SearchBasePath}/documents/{type}/1"))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await SearchAsync(client, "hop dong", type)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("-only")]
    public async Task Search_Should_Return400_When_NothingToMatch(string q)
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        using var response = await client.GetAsync($"{TestConstants.SearchBasePath}?q={Uri.EscapeDataString(q)}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_Should_NotReturn_OtherTenants_Documents()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        string uniqueId = Guid.NewGuid().ToString("N")[..8];
        using var otherClient = await ProvisionTenantClientAsync(rootClient, $"search-iso-{uniqueId}");
        string type = UniqueType();
        await IndexAsync(rootClient, type, "1", "Bí mật của tenant gốc");

        // Tenant B neither sees tenant A's entry…
        (await SearchAsync(otherClient, "bi mat", type)).ShouldBeEmpty();

        // …nor collides with it: the same (EntityType, EntityId) is unique per tenant, not globally.
        await IndexAsync(otherClient, type, "1", "Bí mật của tenant khác");
        (await SearchAsync(otherClient, "bi mat", type)).ShouldHaveSingleItem().Title.ShouldBe("Bí mật của tenant khác");
        (await SearchAsync(rootClient, "bi mat", type)).ShouldHaveSingleItem().Title.ShouldBe("Bí mật của tenant gốc");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static string UniqueType() => $"it-{Guid.NewGuid():N}";

    private static async Task IndexAsync(HttpClient client, string entityType, string entityId, string title, string? summary = null)
    {
        using var response = await client.PutAsJsonAsync($"{TestConstants.SearchBasePath}/documents", new
        {
            entityType,
            entityId,
            title,
            summary,
            body = (string?)null,
            url = $"/items/{entityId}",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent,
            $"indexing failed: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<IReadOnlyList<SearchHit>> SearchAsync(HttpClient client, string q, string entityType)
    {
        using var response = await client.GetAsync(
            $"{TestConstants.SearchBasePath}?q={Uri.EscapeDataString(q)}&entityType={entityType}&pageSize=50");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.DeserializeAsync<PagedResult<SearchHit>>()).Items;
    }

    private async Task<HttpClient> ProvisionTenantClientAsync(HttpClient rootClient, string tenantId)
    {
        string adminEmail = $"{tenantId}-admin@tenant.com";
        using (var created = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        }))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        }
        await TenantProvisioningWait.WaitForProvisioningAsync(rootClient, tenantId);

        for (int attempt = 1; attempt < 30; attempt++)
        {
            try
            {
                return await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(1000);
            }
        }
        return await _auth.CreateAuthenticatedClientAsync(adminEmail, TestConstants.DefaultPassword, tenantId);
    }

    private sealed record SearchHit(string EntityType, string EntityId, string Title, string? Snippet, string? Url, float Rank);
}
