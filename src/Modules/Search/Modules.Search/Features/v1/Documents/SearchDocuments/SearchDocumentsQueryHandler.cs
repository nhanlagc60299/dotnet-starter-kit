using FSH.Framework.Persistence;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Search.Contracts.Dtos;
using FSH.Modules.Search.Contracts.v1.Documents;
using FSH.Modules.Search.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Search.Features.v1.Documents.SearchDocuments;

/// <summary>
/// Ranks with <c>ts_rank_cd</c> plus twice the rank of the typed phrase, so "học sinh" lists "Học sinh giỏi"
/// above "Sinh học". Everything goes through Npgsql's LINQ translation (no raw SQL), so BaseDbContext's
/// tenant query filter applies.
/// </summary>
public sealed class SearchDocumentsQueryHandler(SearchDbContext dbContext)
    : IQueryHandler<SearchDocumentsQuery, PagedResponse<SearchHitDto>>
{
    private const string Config = SearchDbContext.TextSearchConfig;
    private const string HeadlineOptions =
        "StartSel=<mark>, StopSel=</mark>, MaxFragments=2, MaxWords=25, MinWords=8, FragmentDelimiter=\" … \"";

    public async ValueTask<PagedResponse<SearchHitDto>> Handle(SearchDocumentsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var text = SearchText.Parse(query.Q);
        string tsQuery = text.ToTsQuery();
        string? phrase = text.PhraseTsQuery();

        var docs = dbContext.SearchDocuments.AsNoTracking()
            .Where(d => d.Tsv.Matches(EF.Functions.ToTsQuery(Config, tsQuery)));
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            docs = docs.Where(d => d.EntityType == query.EntityType);
        }

        long total = await docs.LongCountAsync(cancellationToken).ConfigureAwait(false);

        var ranked = phrase is null
            ? docs.Select(d => new { Doc = d, Rank = d.Tsv.RankCoverDensity(EF.Functions.ToTsQuery(Config, tsQuery)) })
            : docs.Select(d => new
            {
                Doc = d,
                Rank = d.Tsv.RankCoverDensity(EF.Functions.ToTsQuery(Config, tsQuery))
                    + 2 * d.Tsv.RankCoverDensity(EF.Functions.ToTsQuery(Config, phrase)),
            });

        // ts_headline reads the original text (accents kept); only the page rows pay for it.
        var rows = await ranked
            .OrderByDescending(x => x.Rank).ThenByDescending(x => x.Doc.UpdatedAtUtc).ThenBy(x => x.Doc.Id)
            .Skip(PaginationExtensions.GetOffset(query.PageNumber, query.PageSize))
            .Take(query.PageSize)
            .Select(x => new
            {
                x.Doc.EntityType,
                x.Doc.EntityId,
                x.Doc.Title,
                x.Doc.Url,
                x.Rank,
                Snippet = EF.Functions.ToTsQuery(Config, tsQuery)
                    .GetResultHeadline(Config, (x.Doc.Summary ?? "") + " " + (x.Doc.Body ?? ""), HeadlineOptions),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResponse<SearchHitDto>
        {
            Items = rows.Select(r => new SearchHitDto(
                r.EntityType, r.EntityId, r.Title,
                string.IsNullOrWhiteSpace(r.Snippet) ? null : r.Snippet.Trim(),
                r.Url, r.Rank)).ToList(),
            PageNumber = query.PageNumber,
            PageSize = query.PageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling(total / (double)query.PageSize),
        };
    }
}
