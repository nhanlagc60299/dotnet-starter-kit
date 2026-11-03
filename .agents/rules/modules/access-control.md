# Module: AccessControl

Per-role grants on (subject, action) with an optional row condition that compiles to an EF Core predicate. Module `Order = 910`. **Off by default** (`Modules:AccessControl:Enabled`).

**Entity / DbContext:** `AccessGrant` in `access.access_grants` (`AccessControlDbContext`): `RoleId` (Identity role Id), `Subject`, `Action`, `Condition` (jsonb, null = every row), unique per (tenant, role Id, subject, action).
**Endpoints** (`/api/v1/access-control`): `GET /roles/{role}/grants` (`AccessControl.View`), `PUT /roles/{role}/grants` replaces the whole set, body `[{subject, action, condition?}]` → 204 (`AccessControl.Manage`). `{role}` is the role **name** (case-insensitive), resolved to its Id through `IRoleService` on every call; unknown role → 404.
**Use from another module** (reference `Modules.AccessControl.Contracts`, check `IModuleRegistry.IsEnabled("AccessControl")` first):

```csharp
var query = db.Tickets.Where(t => t.ProjectId == projectId);
var canRead = await accessFilter.BuildAsync(query, "tickets", "read", ct);   // reads the EF model from the query
var tickets = await query.Where(canRead).ToListAsync(ct);   // stays in SQL, tenant filter still applies
```

Grants add up: the conditions of every grant the caller's roles hold are OR-ed; an unconditional grant gives `x => true`, no grant gives `x => false`. Subject `name:*` covers every `name:…`.

## Identity decisions

- **Grants are keyed by role Id**, so they follow the role: a renamed role keeps its grants, and a role created later with a deleted role's name starts with none. The JWT `ClaimTypes.Role` claims carry role **names** (direct and group roles; `IdentityService` issues `r.Name`), so `GrantStore.RoleIdsAsync` maps them to Ids through a per-tenant name → Id map (`IRoleService`, every role of the tenant) cached like the grants. A name that maps to no role reaches nothing. Chosen over role renamed/deleted events from Identity: Identity stays the only source of truth, nothing has to be replayed when AccessControl was disabled while roles changed, and Identity needed no change. Grants of a deleted role stay in the table as unreachable rows.
- **Tenant admin = the built-in `Admin` role** (`RoleConstants.Admin`, seeded in every tenant). Admins get `x => true` from the filter and skip the escalation check.
- **`$CURRENT_USER`** is `ICurrentUser.GetUserId()` (a Guid). Against a `Guid` property it compares as a Guid; against a `string` property as `ToString("D")`, which is how the audit interceptor writes `CreatedBy`/`LastModifiedBy`.

## Condition language

`$eq $ne $in $nin $gt $gte $lt $lte $null $and $or $not`; `{"field": value}` is shorthand for `$eq`. Variables: `$CURRENT_USER`, and `$CURRENT_ROLES` (a list: only with `$eq`/`$ne`, which act as `$in`/`$nin`, or `$in`/`$nin`). Any other string starting with `$` is rejected. Paths are public scalar properties of `T` (string, Guid, bool, numbers, DateTime, DateTimeOffset, enums, nullables), case-insensitive, no navigations. Dates must be UTC. Limits: 200 nodes, 8 levels of `$and/$or/$not`, 100 values per list, 8 000 characters.

## Gotchas

- **Inert until a module uses it.** No module calls `IAccessFilter` yet, so enabling AccessControl and saving grants restricts nothing today. The dashboard says so on the Access rules page.
- **Role changes lag by the token lifetime.** Roles come from the JWT (`JwtOptions:AccessTokenMinutes`, 45 by default), so a user removed from a role keeps its grants, and a demoted Admin keeps `x => true`, until their access token expires or is refreshed. After a rename, tokens still carrying the old name reach nothing until refreshed (fail closed); after a delete + recreate, old tokens carrying the name reach the new role's grants.
- **Role map lags by up to a minute.** The name → Id map is evicted by every PUT (so "create role, set grants" works at once) but not by Identity, which publishes no role events. For up to a minute after a rename or a create elsewhere, the new name reaches nothing; after a delete + recreate, the name can still map to the deleted role's Id (its old grants) for that minute.

- **Fail closed.** A stored condition that does not compile for `T` (unknown property, wrong value type, over limits, bad JSON) contributes `false` and logs a warning. The save-time validator checks structure, operators, operand shapes and variables, but it cannot check property names or value types, because a subject is not tied to a CLR type.
- **Only scalar columns.** `BuildAsync(source, …)` finds the `EntityQueryRootExpression` of `T` in `source` and checks every condition property against the EF model: it must be mapped (`IEntityType.FindProperty`), and its relational type mapping must not be json/jsonb and must hold a scalar after any value converter. Otherwise the condition fails closed instead of throwing in PostgreSQL (a 500). Use the type mapping, not `GetColumnType()`: the read-optimized runtime model throws on the latter. A `source` that is not an EF Core query over `T` (e.g. `list.AsQueryable()`) makes every condition match nothing; unconditional grants and Admin still give `x => true`.
- **NULL follows C# semantics.** EF Core compensates for SQL three-valued logic, so `$ne`, `$nin` and `$not` match rows whose column is NULL (`{"tag": {"$ne": "a"}}` includes `tag IS NULL`), the same as the compiled predicate in memory. Add `"tag": {"$null": false}` to exclude them.
- **Escalation guard (`AccessRules`).** A non-admin may only add, change or remove grants for (subject, action) pairs they hold themselves **without a condition**. Grants left unchanged are not checked, so a manager can edit a role that also has grants beyond their own.
- **Cache.** Grants are cached in `HybridCache` per (tenant, role Id), key `acl:t:{tenant}:r:{roleId}`, and the role map per tenant, key `acl:t:{tenant}:roles` (both kept in the module because `CacheKeys` is in BuildingBlocks); PUT evicts both. Entries expire after **1 minute** in both L1 and L2 (`GrantStore.EntryOptions`), so a revoked grant (and the caller grants the escalation guard reads) can lag by at most about a minute: on peer nodes (no backplane), or when a read overlapping a PUT re-caches the old set after the eviction.
- Values are bound as captured fields (`StrongBox<T>`), so EF sends them as SQL parameters and reuses the query plan per condition shape.
- **Migration `AccessGrantRoleId`** re-keyed name-keyed rows by joining `identity."Roles"` on (TenantId, NormalizedName) and deleted rows whose name matched no role (orphans of earlier renames/deletes).
- Two concurrent PUTs for the same role can race to the unique index; one fails with a 500 and a retry is safe.
- **Duplicate JSON keys.** The validator rejects a condition that repeats a property name. Today the Auditing body capture throws on duplicate keys first, for every endpoint, so the request ends in a 500 before validation.
