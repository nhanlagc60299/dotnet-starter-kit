using System.Linq.Expressions;

namespace FSH.Modules.AccessControl.Contracts;

/// <summary>
/// Turns the current user's grants into a row filter that EF Core translates to SQL:
/// <c>query.Where(await accessFilter.BuildAsync&lt;Ticket&gt;("tickets", "read", ct))</c>.
/// </summary>
public interface IAccessFilter
{
    /// <summary>
    /// The OR of the conditions of every grant the caller's roles hold for (<paramref name="subject"/>,
    /// <paramref name="action"/>): <c>x =&gt; true</c> when one of them is unconditional (or the caller is a tenant
    /// Admin), <c>x =&gt; false</c> when there is none. A stored condition that no longer compiles against
    /// <typeparamref name="T"/> (unknown property, wrong value type) matches nothing.
    /// </summary>
    Task<Expression<Func<T, bool>>> BuildAsync<T>(string subject, string action, CancellationToken ct = default);
}
