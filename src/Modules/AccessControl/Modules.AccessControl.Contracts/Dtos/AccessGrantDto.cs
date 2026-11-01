using System.Text.Json.Nodes;

namespace FSH.Modules.AccessControl.Contracts.Dtos;

/// <summary>
/// One grant of a role: it may do <paramref name="Action"/> on <paramref name="Subject"/> (a subject <c>name:*</c>
/// covers every <c>name:…</c>). <paramref name="Condition"/> is a Strapi-style filter that limits which rows the
/// grant reaches; null means every row.
/// </summary>
/// <param name="Subject">Lowercase, e.g. <c>tickets</c> or <c>content:article</c> (<c>^[a-z][a-z0-9-]*(:[a-z0-9*-]+)?$</c>, ≤ 100).</param>
/// <param name="Action">camelCase verb, e.g. <c>read</c> or <c>readDraft</c> (<c>^[a-z][a-zA-Z]*$</c>, ≤ 50).</param>
/// <param name="Condition">
/// Operators <c>$eq $ne $in $nin $gt $gte $lt $lte $null $and $or $not</c> over the entity's scalar properties
/// (case-insensitive names), with the variables <c>$CURRENT_USER</c> and <c>$CURRENT_ROLES</c>,
/// e.g. <c>{"createdBy": {"$eq": "$CURRENT_USER"}}</c>.
/// </param>
public sealed record AccessGrantDto(string Subject, string Action, JsonObject? Condition = null);
