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
using Microsoft.Extensions.Options;
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
        var a = await CreateRoleRowAsync(admin, "AclA");
        var b = await CreateRoleRowAsync(admin, "AclB");
        var c = await CreateRoleRowAsync(admin, "AclC");
        (string roleA, string roleB, string roleC) = (a.Name, b.Name, c.Name);
        Guid me = Guid.NewGuid();
        await PutAsync(admin, roleA, """
            [{"subject": "access-grants", "action": "read", "condition": {
                "roleId": {"$in": ["ID_A", "ID_B"]},
                "id": {"$ne": "$CURRENT_USER"},
                "$not": {"subject": {"$null": false}, "action": {"$nin": ["read"]}}}},
             {"subject": "tickets", "action": "read"}]
            """.Replace("ID_A", a.Id, StringComparison.Ordinal).Replace("ID_B", b.Id, StringComparison.Ordinal), HttpStatusCode.NoContent);
        await PutAsync(admin, roleB, """
            [{"subject": "access-grants", "action": "read", "condition": {"$or": [{"subject": "files"}, {"SUBJECT": {"$in": ["nothing"]}}]}},
             {"subject": "probe", "action": "read", "condition": {"noSuchProperty": 1}}]
            """, HttpStatusCode.NoContent);
        await PutAsync(admin, roleC, """[{"subject": "files", "action": "read"}, {"subject": "chat", "action": "read"}]""",
            HttpStatusCode.NoContent);
        string[] roleIds = [a.Id, b.Id, c.Id];

        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(Principal(me, roleA, roleB));
        var filter = scope.ServiceProvider.GetRequiredService<IAccessFilter>();
        var db = scope.ServiceProvider.GetRequiredService<AccessControlDbContext>();
        var rows = db.AccessGrants.AsNoTracking().Where(g => roleIds.Contains(g.RoleId));

        // roleA: rows of roles A and B whose id is not my user id; roleB: rows on subject "files" (roleC's). Not chat/read.
        var read = await rows.Where(await filter.BuildAsync(rows, "access-grants", "read")).ToListAsync();
        var label = new Dictionary<string, string> { [a.Id] = "A", [b.Id] = "B", [c.Id] = "C" };
        read.Select(g => $"{label[g.RoleId]}:{g.Subject}/{g.Action}").ShouldBe(
            ["A:access-grants/read", "A:tickets/read", "B:access-grants/read", "B:probe/read", "C:files/read"], ignoreOrder: true);

        // Unconditional grant → every row; no grant → none; a condition that does not fit the entity → none.
        (await rows.Where(await filter.BuildAsync(rows, "tickets", "read")).CountAsync()).ShouldBe(6);
        (await rows.Where(await filter.BuildAsync(rows, "chat", "read")).CountAsync()).ShouldBe(0);
        (await rows.Where(await filter.BuildAsync(rows, "probe", "read")).CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AccessFilter_Should_SeeNewGrants_When_RoleGrantsReplaced()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        var role = await CreateRoleRowAsync(admin, "AclCache");
        await PutAsync(admin, role.Name, """[{"subject": "docs", "action": "read", "condition": {"subject": "none"}}]""", HttpStatusCode.NoContent);

        (await CountVisibleAsync(role.Name, role.Id)).ShouldBe(0); // warms the (tenant, role) cache entry
        await PutAsync(admin, role.Name, """[{"subject": "docs", "action": "read", "condition": {"subject": "docs"}}]""", HttpStatusCode.NoContent);
        (await CountVisibleAsync(role.Name, role.Id)).ShouldBe(1); // the write evicted it
    }

    [Fact]
    public async Task Grants_Should_FollowRole_When_RoleRenamed()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        var role = await CreateRoleRowAsync(admin, "AclRename");
        await PutAsync(admin, role.Name, """[{"subject": "docs", "action": "read"}]""", HttpStatusCode.NoContent);

        string renamed = $"zAclRenamed-{Guid.NewGuid():N}"[..21];
        await UpsertRoleAsync(admin, role.Id, renamed);

        (await GetAsync(admin, renamed)).ShouldHaveSingleItem().Subject.ShouldBe("docs");
        await PutAsync(admin, role.Name, "[]", HttpStatusCode.NotFound); // the old name is gone
        (await CountVisibleAsync(renamed, role.Id)).ShouldBe(1, "a token carrying the new name reaches the grants");
        (await CountVisibleAsync(role.Name, role.Id)).ShouldBe(0, "a token still carrying the old name reaches nothing");
    }

    [Fact]
    public async Task Grants_Should_NotBeInherited_When_RoleDeletedAndNameReused()
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        var old = await CreateRoleRowAsync(admin, "AclReuse");
        await PutAsync(admin, old.Name, """[{"subject": "docs", "action": "read"}]""", HttpStatusCode.NoContent);

        using (var deleted = await admin.DeleteAsync($"{TestConstants.IdentityBasePath}/roles/{old.Id}"))
            deleted.IsSuccessStatusCode.ShouldBeTrue(await deleted.Content.ReadAsStringAsync());
        var reused = await UpsertRoleAsync(admin, string.Empty, old.Name);
        reused.Id.ShouldNotBe(old.Id);

        (await GetAsync(admin, old.Name)).ShouldBeEmpty();
        (await CountVisibleAsync(old.Name, old.Id)).ShouldBe(0, "the deleted role's grants must not reach the new role");
    }

    [Fact]
    public async Task AccessFilter_Should_MatchEverything_ForTenantAdmin_And_NothingWhenAnonymous()
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(Principal(Guid.NewGuid(), "Admin"));
        var filter = scope.ServiceProvider.GetRequiredService<IAccessFilter>();

        (await filter.BuildAsync(Array.Empty<AccessGrant>().AsQueryable(), "anything", "read")).Compile()(null!).ShouldBeTrue();

        using var anonymousScope = _factory.Services.CreateScope();
        anonymousScope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(new ClaimsPrincipal(new ClaimsIdentity()));
        var anonymous = anonymousScope.ServiceProvider.GetRequiredService<IAccessFilter>();
        (await anonymous.BuildAsync(Array.Empty<AccessGrant>().AsQueryable(), "anything", "read")).Compile()(null!).ShouldBeFalse();
    }

    [Fact]
    public async Task AccessFilter_Should_KeepCSharpNullSemantics_When_ColumnIsNull()
    {
        var (role, batch) = await SeedProbesAsync("""
            [{"subject": "ne", "action": "read", "condition": {"tag": {"$ne": "a"}}},
             {"subject": "nin", "action": "read", "condition": {"tag": {"$nin": ["a"]}}},
             {"subject": "notgt", "action": "read", "condition": {"$not": {"rank": {"$gt": 5}}}},
             {"subject": "roles", "action": "read", "condition": {"tag": {"$in": "$CURRENT_ROLES"}}}]
            """);
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(Principal(Guid.NewGuid(), role));
        var filter = scope.ServiceProvider.GetRequiredService<IAccessFilter>();
        using var db = ProbeDb(scope);
        var probes = db.Probes.AsNoTracking().Where(p => p.Batch == batch);

        // SQL agrees with the same predicate run in memory (C# semantics): NULL is "not equal" and "not greater".
        var all = await probes.ToListAsync();
        foreach (var (subject, expected) in new[]
                 {
                     ("ne", new[] { "null", "role", "ten" }), ("nin", ["null", "role", "ten"]),
                     ("notgt", ["null", "one", "role"]), ("roles", ["role"]),
                 })
        {
            var predicate = await filter.BuildAsync(probes, subject, "read");
            (await probes.Where(predicate).Select(p => p.Name).ToListAsync()).ShouldBe(expected, ignoreOrder: true, subject);
            all.AsQueryable().Where(predicate).Select(p => p.Name).ShouldBe(expected, ignoreOrder: true, subject);
        }
    }

    [Fact]
    public async Task AccessFilter_Should_MatchNothing_When_ConditionUsesNonScalarOrUnmappedProperty()
    {
        var (role, batch) = await SeedProbesAsync("""
            [{"subject": "json", "action": "read", "condition": {"data": {"$eq": "x"}}},
             {"subject": "unmapped", "action": "read", "condition": {"shout": "ONE"}},
             {"subject": "mixed:x", "action": "read", "condition": {"data": "x"}},
             {"subject": "mixed:*", "action": "read", "condition": {"name": "ten"}}]
            """);
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(Principal(Guid.NewGuid(), role));
        var filter = scope.ServiceProvider.GetRequiredService<IAccessFilter>();
        using var db = ProbeDb(scope);
        var probes = db.Probes.AsNoTracking().Where(p => p.Batch == batch);

        // Without the EF model check these throw in PostgreSQL (jsonb = text) or at translation (unmapped): a 500.
        (await probes.Where(await filter.BuildAsync(probes, "json", "read")).CountAsync()).ShouldBe(0);
        (await probes.Where(await filter.BuildAsync(probes, "unmapped", "read")).CountAsync()).ShouldBe(0);
        // A bad grant only drops itself: the other grant covering the subject still applies.
        (await probes.Where(await filter.BuildAsync(probes, "mixed:x", "read")).Select(p => p.Name).ToListAsync()).ShouldBe(["ten"]);
        // Not an EF Core query: conditions cannot be checked against columns, so they match nothing.
        (await filter.BuildAsync(Array.Empty<AclProbe>().AsQueryable(), "mixed:x", "read")).Compile()(new AclProbe { Name = "ten" })
            .ShouldBeFalse();
    }

    /// <summary>
    /// A role holding <paramref name="grantsJson"/> and four probe rows of a fresh batch: one (tag a, rank 1),
    /// ten (tag b, rank 10), null (no tag or rank, jsonb data), role (tag = the role's name, rank 5).
    /// </summary>
    private async Task<(string Role, Guid Batch)> SeedProbesAsync(string grantsJson)
    {
        using var admin = await _auth.CreateRootAdminClientAsync();
        string role = await CreateRoleAsync(admin, "AclProbe");
        await PutAsync(admin, role, grantsJson, HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        using var db = ProbeDb(scope);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS acl_probe;
            CREATE TABLE IF NOT EXISTS acl_probe.probes (
                "Id" uuid PRIMARY KEY, "Batch" uuid NOT NULL, "Name" text NOT NULL, "Tag" text NULL, "Rank" integer NULL, "Data" jsonb NULL);
            """);
        Guid batch = Guid.NewGuid();
        db.Probes.AddRange(
            new AclProbe { Id = Guid.NewGuid(), Batch = batch, Name = "one", Tag = "a", Rank = 1 },
            new AclProbe { Id = Guid.NewGuid(), Batch = batch, Name = "ten", Tag = "b", Rank = 10 },
            new AclProbe { Id = Guid.NewGuid(), Batch = batch, Name = "null", Data = """{"k": 1}""" },
            new AclProbe { Id = Guid.NewGuid(), Batch = batch, Name = "role", Tag = role, Rank = 5 });
        await db.SaveChangesAsync();
        return (role, batch);
    }

    private static AclProbeDbContext ProbeDb(IServiceScope scope) =>
        new(new DbContextOptionsBuilder<AclProbeDbContext>()
            .UseNpgsql(scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString).Options);

    /// <summary>How many of <paramref name="roleId"/>'s grant rows a caller holding <paramref name="role"/> sees on docs/read.</summary>
    private async Task<int> CountVisibleAsync(string role, string roleId)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        scope.ServiceProvider.GetRequiredService<ICurrentUserInitializer>().SetCurrentUser(Principal(Guid.NewGuid(), role));
        var filter = scope.ServiceProvider.GetRequiredService<IAccessFilter>();
        var db = scope.ServiceProvider.GetRequiredService<AccessControlDbContext>();
        var rows = db.AccessGrants.Where(g => g.RoleId == roleId);
        return await rows.Where(await filter.BuildAsync(rows, "docs", "read")).CountAsync();
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

    private static async Task<string> CreateRoleAsync(HttpClient admin, string prefix, params string[] permissions) =>
        (await CreateRoleRowAsync(admin, prefix, permissions)).Name;

    private static async Task<RoleRow> CreateRoleRowAsync(HttpClient admin, string prefix, params string[] permissions)
    {
        // "z" first: GET /identity/roles pages by name, and other tests expect Admin/Basic on the first page.
        var role = await UpsertRoleAsync(admin, string.Empty, $"z{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 10)]);
        if (permissions.Length > 0)
        {
            using var set = await admin.PutAsJsonAsync($"{TestConstants.IdentityBasePath}/{role.Id}/permissions",
                new { roleId = role.Id, permissions });
            set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        }
        return role;
    }

    /// <summary>Creates a role (empty id) or renames one.</summary>
    private static async Task<RoleRow> UpsertRoleAsync(HttpClient admin, string id, string name)
    {
        using var response = await admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/roles",
            new { id, name, description = "access-control test role" });
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<RoleRow>();
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

    /// <summary>A small table to check how conditions translate: nullable columns, a jsonb one and an unmapped property.</summary>
    public sealed class AclProbe
    {
        public Guid Id { get; set; }
        public Guid Batch { get; set; }
        public string Name { get; set; } = default!;
        public string? Tag { get; set; }
        public int? Rank { get; set; }
        public string? Data { get; set; }
        public string Shout => Name.ToUpperInvariant();
    }

    private sealed class AclProbeDbContext(DbContextOptions<AclProbeDbContext> options) : DbContext(options)
    {
        public DbSet<AclProbe> Probes => Set<AclProbe>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<AclProbe>(e =>
            {
                e.ToTable("probes", "acl_probe");
                e.Property(p => p.Data).HasColumnType("jsonb");
            });
    }

    private sealed record RoleRow(string Id, string Name);
}
