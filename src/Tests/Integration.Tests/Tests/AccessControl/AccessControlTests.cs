using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.AccessControl.Contracts;
using FSH.Modules.AccessControl.Contracts.Authorization;
using FSH.Modules.AccessControl.Data;
using FSH.Modules.AccessControl.Domain;
using FSH.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Integration.Tests.Tests.AccessControl;

/// <summary>
/// Grants API (PUT/GET, validation, escalation guard) and <see cref="IAccessFilter"/> translated by EF Core and run
/// on real PostgreSQL. Every test works on its own freshly created roles, so rows from other tests never match.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class AccessControlTests
{
    private readonly FshWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public AccessControlTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    #region Grants API

    [Fact]
    public async Task PutGrants_Should_ReplaceWholeSet_And_GetReturnsIt()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string role = await CreateRoleAsync(admin, "AclRole");

        await PutAsync(admin, role, """
            [{"subject": "tickets", "action": "read"},
             {"subject": "content:*", "action": "update", "condition": {"createdBy": {"$eq": "$CURRENT_USER"}}},
             {"subject": "files", "action": "read", "condition": {}}]
            """, HttpStatusCode.NoContent);

        var grants = await GetAsync(admin, role);
        grants.Select(g => $"{g.Subject}/{g.Action}").ShouldBe(["content:*/update", "files/read", "tickets/read"]);
        grants[0].Condition!["createdBy"]!["$eq"]!.GetValue<string>().ShouldBe("$CURRENT_USER");
        grants[1].Condition.ShouldBeNull("{} means no condition");
        grants[2].Condition.ShouldBeNull();

        // Replace: the role name matches case-insensitively, and the new set replaces the old one entirely.
        await PutAsync(admin, role.ToUpperInvariant(), """[{"subject": "tickets", "action": "read", "condition": {"id": {"$null": false}}}]""",
            HttpStatusCode.NoContent);

        var replaced = await GetAsync(admin, role);
        replaced.ShouldHaveSingleItem().Condition!.ToJsonString().ShouldBe("""{"id":{"$null":false}}""");
    }

    [Theory]
    [InlineData("""[{"subject": "tickets", "action": "read", "condition": {"a": {"$regex": ".*"}}}]""")]
    [InlineData("""[{"subject": "tickets", "action": "read", "condition": {"$or": []}}]""")]
    [InlineData("""[{"subject": "tickets", "action": "read", "condition": {"a": "$CURRENT_USERS"}}]""")]
    [InlineData("""[{"subject": "tickets", "action": "read", "condition": {"a": {"$in": []}}}]""")]
    [InlineData("""[{"subject": "Tickets", "action": "read"}]""")]
    [InlineData("""[{"subject": "tickets", "action": "read-all"}]""")]
    [InlineData("""[{"subject": "<b>x</b>", "action": "read"}]""")]
    [InlineData("""[{"subject": "tickets", "action": "read"}, {"subject": "tickets", "action": "read", "condition": {"a": 1}}]""")]
    public async Task PutGrants_Should_Return400_When_GrantInvalid(string body)
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string role = await CreateRoleAsync(admin, "AclBad");

        await PutAsync(admin, role, body, HttpStatusCode.BadRequest);
        (await GetAsync(admin, role)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PutGrants_Should_Return400_When_ConditionOverLimits()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string role = await CreateRoleAsync(admin, "AclBig");
        var or = new JsonArray(Enumerable.Range(0, 201).Select(i => (JsonNode?)new JsonObject { ["a"] = i }).ToArray());
        var body = new JsonArray(new JsonObject { ["subject"] = "tickets", ["action"] = "read", ["condition"] = new JsonObject { ["$or"] = or } });

        await PutAsync(admin, role, body.ToJsonString(), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grants_Should_Return404_When_RoleUnknown()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string missing = $"NoSuchRole-{Guid.NewGuid():N}";

        await PutAsync(admin, missing, "[]", HttpStatusCode.NotFound);
        using var response = await admin.GetAsync($"{TestConstants.AccessControlBasePath}/roles/{missing}/grants");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutGrants_Should_OnlyLetNonAdminChange_GrantsTheyHoldUnconditionally()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string managerRole = await CreateRoleAsync(admin, "AclManager", AccessControlPermissions.AccessControl.View,
            AccessControlPermissions.AccessControl.Manage);
        string target = await CreateRoleAsync(admin, "AclTarget");
        await PutAsync(admin, managerRole, """
            [{"subject": "tickets", "action": "read"},
             {"subject": "content:*", "action": "update"},
             {"subject": "files", "action": "read", "condition": {"createdBy": "$CURRENT_USER"}}]
            """, HttpStatusCode.NoContent);
        await PutAsync(admin, target, """[{"subject": "chat", "action": "read"}]""", HttpStatusCode.NoContent);

        var (email, password, userId) = await CreateActiveUserAsync($"aclmgr-{Guid.NewGuid():N}"[..20]);
        await AssignRoleAsync(admin, userId, managerRole);
        using var manager = await _auth.CreateAuthenticatedClientAsync(email, password);

        // Covered: same action, held without a condition (narrowing with a condition is fine; content:* covers content:page).
        // chat/read is not the manager's, but it is kept unchanged, so it is not an escalation.
        await PutAsync(manager, target, """
            [{"subject": "chat", "action": "read"},
             {"subject": "tickets", "action": "read", "condition": {"createdBy": "$CURRENT_USER"}},
             {"subject": "content:page", "action": "update"}]
            """, HttpStatusCode.NoContent);

        // Not covered: an action the manager lacks, one the manager only holds with a condition, and removing chat/read.
        await PutAsync(manager, target, """
            [{"subject": "chat", "action": "read"}, {"subject": "tickets", "action": "update"}]
            """, HttpStatusCode.Forbidden);
        await PutAsync(manager, target, """
            [{"subject": "chat", "action": "read"}, {"subject": "files", "action": "read", "condition": {"createdBy": "$CURRENT_USER"}}]
            """, HttpStatusCode.Forbidden);
        await PutAsync(manager, target, """[{"subject": "tickets", "action": "read"}]""", HttpStatusCode.Forbidden);

        // Nor can the manager widen their own role.
        await PutAsync(manager, managerRole, """
            [{"subject": "tickets", "action": "read"}, {"subject": "content:*", "action": "update"},
             {"subject": "files", "action": "read"}]
            """, HttpStatusCode.Forbidden);

        (await GetAsync(admin, target)).Select(g => $"{g.Subject}/{g.Action}")
            .ShouldBe(["chat/read", "content:page/update", "tickets/read"]);
    }

    [Fact]
    public async Task Grants_Should_Return403_When_CallerLacksPermission()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string role = await CreateRoleAsync(admin, "AclNoPerm");
        var (email, password, userId) = await CreateActiveUserAsync($"aclnone-{Guid.NewGuid():N}"[..20]);
        await AssignRoleAsync(admin, userId, role);
        using var user = await _auth.CreateAuthenticatedClientAsync(email, password);

        using var get = await user.GetAsync($"{TestConstants.AccessControlBasePath}/roles/{role}/grants");
        get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await PutAsync(user, role, "[]", HttpStatusCode.Forbidden);
    }

    #endregion

    #region IAccessFilter on PostgreSQL

    [Fact]
    public async Task AccessFilter_Should_TranslateToSql_And_OrGrantsAcrossRoles()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string roleA = await CreateRoleAsync(admin, "AclA");
        string roleB = await CreateRoleAsync(admin, "AclB");
        string roleC = await CreateRoleAsync(admin, "AclC");
        Guid me = Guid.NewGuid();
        await PutAsync(admin, roleA, """
            [{"subject": "access-grants", "action": "read", "condition": {
                "role": {"$eq": "$CURRENT_ROLES"},
                "id": {"$ne": "$CURRENT_USER"},
                "$not": {"condition": {"$null": false}, "action": {"$nin": ["read"]}}}},
             {"subject": "tickets", "action": "read"}]
            """, HttpStatusCode.NoContent);
        await PutAsync(admin, roleB, """
            [{"subject": "access-grants", "action": "read", "condition": {"$or": [{"subject": "files"}, {"SUBJECT": {"$in": ["nothing"]}}]}},
             {"subject": "probe", "action": "read", "condition": {"noSuchProperty": 1}}]
            """, HttpStatusCode.NoContent);
        await PutAsync(admin, roleC, """[{"subject": "files", "action": "read"}, {"subject": "chat", "action": "read"}]""",
            HttpStatusCode.NoContent);
        string[] roles = [roleA, roleB, roleC];

        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(Principal(me, roleA, roleB));
        var filter = scope.ServiceProvider.GetRequiredService<IAccessFilter>();
        var db = scope.ServiceProvider.GetRequiredService<AccessControlDbContext>();
        var rows = db.AccessGrants.AsNoTracking().Where(g => roles.Contains(g.Role));

        // roleA: rows of my roles (A, B) whose id is not my user id; roleB: rows on subject "files" (roleC's). Not chat/read.
        var read = await rows.Where(await filter.BuildAsync<AccessGrant>("access-grants", "read")).ToListAsync();
        var label = new Dictionary<string, string> { [roleA] = "A", [roleB] = "B", [roleC] = "C" };
        read.Select(g => $"{label[g.Role]}:{g.Subject}/{g.Action}").ShouldBe(
            ["A:access-grants/read", "A:tickets/read", "B:access-grants/read", "B:probe/read", "C:files/read"], ignoreOrder: true);

        // Unconditional grant → every row; no grant → none; a condition that does not fit the entity → none.
        (await rows.Where(await filter.BuildAsync<AccessGrant>("tickets", "read")).CountAsync()).ShouldBe(6);
        (await rows.Where(await filter.BuildAsync<AccessGrant>("chat", "read")).CountAsync()).ShouldBe(0);
        (await rows.Where(await filter.BuildAsync<AccessGrant>("probe", "read")).CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AccessFilter_Should_SeeNewGrants_When_RoleGrantsReplaced()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string role = await CreateRoleAsync(admin, "AclCache");
        await PutAsync(admin, role, """[{"subject": "docs", "action": "read", "condition": {"subject": "none"}}]""", HttpStatusCode.NoContent);

        (await CountVisibleAsync(role)).ShouldBe(0); // warms the (tenant, role) cache entry
        await PutAsync(admin, role, """[{"subject": "docs", "action": "read", "condition": {"subject": "docs"}}]""", HttpStatusCode.NoContent);
        (await CountVisibleAsync(role)).ShouldBe(1); // the write evicted it
    }

    [Fact]
    public async Task AccessFilter_Should_MatchEverything_ForTenantAdmin_And_NothingWhenAnonymous()
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(Principal(Guid.NewGuid(), "Admin"));
        var filter = scope.ServiceProvider.GetRequiredService<IAccessFilter>();

        (await filter.BuildAsync<AccessGrant>("anything", "read")).Compile()(null!).ShouldBeTrue();

        using var anonymousScope = _factory.Services.CreateScope();
        anonymousScope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(new ClaimsPrincipal(new ClaimsIdentity()));
        var anonymous = anonymousScope.ServiceProvider.GetRequiredService<IAccessFilter>();
        (await anonymous.BuildAsync<AccessGrant>("anything", "read")).Compile()(null!).ShouldBeFalse();
    }

    private async Task<int> CountVisibleAsync(string role)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(Principal(Guid.NewGuid(), role));
        var filter = scope.ServiceProvider.GetRequiredService<IAccessFilter>();
        var db = scope.ServiceProvider.GetRequiredService<AccessControlDbContext>();
        return await db.AccessGrants.Where(g => g.Role == role).Where(await filter.BuildAsync<AccessGrant>("docs", "read")).CountAsync();
    }

    #endregion

    // ─── helpers ─────────────────────────────────────────────────────

    private static ClaimsPrincipal Principal(Guid userId, params string[] roles) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), .. roles.Select(r => new Claim(ClaimTypes.Role, r))],
            "Test", ClaimTypes.Name, ClaimTypes.Role));

    private static async Task PutAsync(HttpClient client, string role, string json, HttpStatusCode expected)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PutAsync($"{TestConstants.AccessControlBasePath}/roles/{Uri.EscapeDataString(role)}/grants", content);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync());
    }

    private static async Task<List<GrantRow>> GetAsync(HttpClient client, string role)
    {
        using var response = await client.GetAsync($"{TestConstants.AccessControlBasePath}/roles/{Uri.EscapeDataString(role)}/grants");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<List<GrantRow>>();
    }

    private static async Task<string> CreateRoleAsync(HttpClient admin, string prefix, params string[] permissions)
    {
        // "z" first: GET /identity/roles pages by name, and other tests expect Admin/Basic on the first page.
        string name = $"z{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 10)];
        using var created = await admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/roles",
            new { id = string.Empty, name, description = "access-control test role" });
        var role = await created.DeserializeAsync<RoleRow>();
        if (permissions.Length > 0)
        {
            using var set = await admin.PutAsJsonAsync($"{TestConstants.IdentityBasePath}/{role.Id}/permissions",
                new { roleId = role.Id, permissions });
            set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        }
        return role.Name;
    }

    private static async Task AssignRoleAsync(HttpClient admin, string userId, string roleName)
    {
        using var response = await admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/users/{userId}/roles",
            new { userId, userRoles = new[] { new { roleName, enabled = true } } });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Seeds a confirmed, active user; the tenant context is set inline (AsyncLocal, see integration-testing.md).</summary>
    private async Task<(string Email, string Password, string UserId)> CreateActiveUserAsync(string handle)
    {
        const string password = TestConstants.DefaultPassword;
        string email = $"{handle}@example.com";
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        var user = new FshUser
        {
            FirstName = "Access",
            LastName = "Probe",
            Email = email,
            UserName = handle,
            EmailConfirmed = true,
            IsActive = true,
        };
        var result = await userManager.CreateAsync(user, password);
        result.Succeeded.ShouldBeTrue(string.Join(", ", result.Errors.Select(e => e.Description)));
        return (email, password, user.Id);
    }

    private sealed record GrantRow(string Subject, string Action, JsonObject? Condition);

    private sealed record RoleRow(string Id, string Name);
}
