using System.Linq.Expressions;
using FSH.Framework.Core.Context;
using FSH.Modules.AccessControl.Conditions;
using FSH.Modules.AccessControl.Contracts;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.AccessControl.Services;

internal sealed partial class AccessFilter(ICurrentUser currentUser, GrantStore store, ILogger<AccessFilter> logger) : IAccessFilter
{
    public async Task<Expression<Func<T, bool>>> BuildAsync<T>(string subject, string action, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!currentUser.IsAuthenticated()) return _ => false;
        var roles = AccessRules.Roles(currentUser);
        if (AccessRules.IsAdmin(roles)) return _ => true;

        var conditions = new List<string>();
        foreach (string role in roles)
        {
            foreach (var grant in await store.GetAsync(role, ct).ConfigureAwait(false))
            {
                if (grant.Action != action || !AccessRules.SubjectCovers(grant.Subject, subject)) continue;
                if (grant.Condition is null) return _ => true;
                conditions.Add(grant.Condition);
            }
        }

        var x = Expression.Parameter(typeof(T), "x");
        var variables = new ConditionVariables(currentUser.GetUserId(), roles);
        var bodies = new List<Expression>(conditions.Count);
        foreach (string condition in conditions)
        {
            if (ConditionCompiler.TryBuild(condition, x, variables, out string? problem) is { } body)
                bodies.Add(body);
            else
                LogInvalidCondition(logger, subject, action, typeof(T).Name, problem);
        }
        return Expression.Lambda<Func<T, bool>>(ConditionCompiler.AnyOf(bodies), x);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A grant condition for {Subject}/{Action} does not apply to {EntityType} and matches nothing: {Problem}")]
    private static partial void LogInvalidCondition(ILogger logger, string subject, string action, string entityType, string? problem);
}
