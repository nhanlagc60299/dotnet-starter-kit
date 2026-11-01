using FSH.Framework.Shared.Persistence;
using FSH.Modules.Search.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Search.Contracts.v1.Documents;

/// <summary>
/// Full-text search over the tenant's index. <paramref name="Q"/> uses web-search syntax:
/// words (all required, accents optional), <c>"exact phrase"</c>, <c>-exclude</c>, <c>or</c>;
/// the last word also matches as a prefix unless the text ends with a space.
/// </summary>
public sealed record SearchDocumentsQuery(
    string Q,
    string? EntityType = null,
    int PageNumber = 1,
    int PageSize = 20) : IQuery<PagedResponse<SearchHitDto>>;
