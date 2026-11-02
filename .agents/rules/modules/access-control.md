# Module: AccessControl

Per-role grants on (subject, action) with an optional row condition that compiles to an EF Core predicate. Module `Order = 910`. **Off by default** (`Modules:AccessControl:Enabled`).

**Entity / DbContext:** `AccessGrant` in `access.access_grants` (`AccessControlDbContext`): `Role`, `Subject`, `Action`, `Condition` (jsonb, null = every row), unique per (tenant, role, subject, action).
**Endpoints** (`/api/v1/access-control`): `GET /roles/{role}/grants` (`AccessControl.View`), `PUT /roles/{role}/grants` replaces the whole set, body `[{subject, action, condition?}]` → 204 (`AccessControl.Manage`). Unknown role → 404.
**Use from another module** (reference `Modules.AccessControl.Contracts`, check `IModuleRegistry.IsEnabled("AccessControl")` first):

```csharp
var canRead = await accessFilter.BuildAsync<Ticket>("tickets", "read", ct);
var tickets = await db.Tickets.Where(canRead).ToListAsync(ct);   // stays in SQL, tenant filter still applies
```

Grants add up: the conditions of every grant the caller's roles hold are OR-ed; an unconditional grant gives `x => true`, no grant gives `x => false`. Subject `name:*` covers every `name:…`.

## Identity decisions

- **Grants are keyed by role name**, the value FSH puts in the JWT `ClaimTypes.Role` claims (direct and group roles; `IdentityService` issues `r.Name`). PUT/GET match the name case-insensitively and store Identity's spelling.
- **Tenant admin = the built-in `Admin` role** (`RoleConstants.Admin`, seeded in every tenant). Admins get `x => true` from the filter and skip the escalation check.
- **`$CURRENT_USER`** is `ICurrentUser.GetUserId()` (a Guid). Against a `Guid` property it compares as a Guid; against a `string` property as `ToString("D")`, which is how the audit interceptor writes `CreatedBy`/`LastModifiedBy`.

## Condition language

`$eq $ne $in $nin $gt $gte $lt $lte $null $and $or $not`; `{"field": value}` is shorthand for `$eq`. Variables: `$CURRENT_USER`, and `$CURRENT_ROLES` (a list: only with `$eq`/`$ne`, which act as `$in`/`$nin`, or `$in`/`$nin`). Any other string starting with `$` is rejected. Paths are public scalar properties of `T` (string, Guid, bool, numbers, DateTime, DateTimeOffset, enums, nullables), case-insensitive, no navigations. Dates must be UTC. Limits: 200 nodes, 8 levels of `$and/$or/$not`, 100 values per list, 8 000 characters.

## Gotchas

- **Inert until a module uses it.** No module calls `IAccessFilter` yet, so enabling AccessControl and saving grants restricts nothing today. The dashboard says so on the Access rules page.
- **Role changes lag by the token lifetime.** Roles come from the JWT (`JwtOptions:AccessTokenMinutes`, 45 by default), so a user removed from a role keeps its grants, and a demoted Admin keeps `x => true`, until their access token expires or is refreshed.

- **Fail closed.** A stored condition that does not compile for `T` (unknown property, wrong value type, over limits, bad JSON) contributes `false` and logs a warning. The save-time validator checks structure, operators, operand shapes and variables, but it cannot check property names or value types, because a subject is not tied to a CLR type.
- **Only EF-mapped properties work.** The compiler accepts any public scalar property; one that EF does not map fails at query translation.
- **Escalation guard (`AccessRules`).** A non-admin may only add, change or remove grants for (subject, action) pairs they hold themselves **without a condition**. Grants left unchanged are not checked, so a manager can edit a role that also has grants beyond their own.
- **Cache.** Grants are cached in `HybridCache` per (tenant, role), key `acl:t:{tenant}:r:{role}` (kept in the module because `CacheKeys` is in BuildingBlocks), and evicted on PUT. Entries expire after **1 minute** in both L1 and L2 (`GrantStore.EntryOptions`), so a revoked grant (and the caller grants the escalation guard reads) can lag by at most about a minute: on peer nodes (no backplane), or when a read overlapping a PUT re-caches the old set after the eviction.
- Values are bound as captured fields (`StrongBox<T>`), so EF sends them as SQL parameters and reuses the query plan per condition shape.
- **Grants follow the role NAME, not the role.** Identity lets admins rename and delete custom roles and publishes no role events, so this module never hears about it. Renaming a role silently drops its grants: they stay stored under the old name and its holders lose access. Deleting a role leaves its grants behind, and a new role created later with the same name inherits them. Until this is fixed, re-PUT grants after a rename and clear them (PUT `[]`) before deleting a role. Intended follow-up: Identity publishes `RoleRenamed`/`RoleDeleted` integration events that this module handles (move or delete the grants), or grants are keyed by role Id, with the JWT carrying role ids or the ids resolved from names per request.
- Two concurrent PUTs for the same role can race to the unique index; one fails with a 500 and a retry is safe.
- **Duplicate JSON keys.** The validator rejects a condition that repeats a property name. Today the Auditing body capture throws on duplicate keys first, for every endpoint, so the request ends in a 500 before validation.
