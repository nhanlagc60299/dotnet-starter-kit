using System.Linq.Expressions;

namespace FSH.Modules.AccessControl.Contracts;

/// <summary>
/// Turns the current user's grants into a row filter that EF Core translates to SQL:
/// <c>query.Where(await accessFilter.BuildAsync(query, "tickets", "read", ct))</c>.
/// </summary>
public interface IAccessFilter
{
    /// <summary>
    /// The OR of the conditions of every grant the caller's roles hold for (<paramref name="subject"/>,
    /// <paramref name="action"/>): <c>x =&gt; true</c> when one of them is unconditional (or the caller is a tenant
    /// Admin), <c>x =&gt; false</c> when there is none. A stored condition that no longer compiles against
    /// <typeparamref name="T"/> (unknown property, wrong value type, a property EF Core does not map or maps to a
    /// non-scalar column such as jsonb) matches nothing.
    /// </summary>
    /// <param name="source">
    /// The EF Core query the filter will be applied to (e.g. <c>db.Tickets</c>). Only its model is read, to check
    /// condition properties against their columns; a query that is not rooted in an EF Core entity set of
    /// <typeparamref name="T"/> makes every condition match nothing.
    /// </param>
    /// <param name="subject">The subject, e.g. <c>tickets</c>.</param>
    /// <param name="action">The action, e.g. <c>read</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Expression<Func<T, bool>>> BuildAsync<T>(IQueryable<T> source, string subject, string action, CancellationToken ct = default);
}
