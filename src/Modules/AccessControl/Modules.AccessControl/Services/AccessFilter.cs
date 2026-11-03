using System.Linq.Expressions;
using FSH.Framework.Core.Context;
using FSH.Modules.AccessControl.Conditions;
using FSH.Modules.AccessControl.Contracts;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.AccessControl.Services;

internal sealed partial class AccessFilter(ICurrentUser currentUser, GrantStore store, ILogger<AccessFilter> logger) : IAccessFilter
{
    public async Task<Expression<Func<T, bool>>> BuildAsync<T>(IQueryable<T> source, string subject, string action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!currentUser.IsAuthenticated()) return _ => false;
        var roles = AccessRules.Roles(currentUser);
        if (AccessRules.IsAdmin(roles)) return _ => true;

        var conditions = new List<string>();
        foreach (string roleId in await store.RoleIdsAsync(roles, ct).ConfigureAwait(false))
        {
            foreach (var grant in await store.GetAsync(roleId, ct).ConfigureAwait(false))
            {
                if (grant.Action != action || !AccessRules.SubjectCovers(grant.Subject, subject)) continue;
                if (grant.Condition is null) return _ => true;
                conditions.Add(grant.Condition);
            }
        }

        var x = Expression.Parameter(typeof(T), "x");
        var variables = new ConditionVariables(currentUser.GetUserId(), roles);
        var entityType = conditions.Count == 0 ? null : RootFinder.EntityType(source.Expression, typeof(T));
        var bodies = new List<Expression>(conditions.Count);
        foreach (string condition in conditions)
        {
            string? problem = "The query is not rooted in an EF Core entity set of this type.";
            if (entityType is not null && ConditionCompiler.TryBuild(condition, x, variables, entityType, out problem) is { } body)
                bodies.Add(body);
            else
                LogInvalidCondition(logger, subject, action, typeof(T).Name, problem);
        }
        return Expression.Lambda<Func<T, bool>>(ConditionCompiler.AnyOf(bodies), x);
    }

    /// <summary>Finds the EF Core entity set of a type the query reads from (<c>db.Tickets</c> is one).</summary>
    private sealed class RootFinder(Type type) : ExpressionVisitor
    {
        private IEntityType? _found;

        public static IEntityType? EntityType(Expression query, Type type)
        {
            var finder = new RootFinder(type);
            finder.Visit(query);
            return finder._found;
        }

        // Query roots are leaves; other extension nodes are not descended into (they may not be reducible).
        protected override Expression VisitExtension(Expression node)
        {
            if (node is EntityQueryRootExpression root && root.EntityType.ClrType == type) _found ??= root.EntityType;
            return node;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A grant condition for {Subject}/{Action} does not apply to {EntityType} and matches nothing: {Problem}")]
    private static partial void LogInvalidCondition(ILogger logger, string subject, string action, string entityType, string? problem);
}
