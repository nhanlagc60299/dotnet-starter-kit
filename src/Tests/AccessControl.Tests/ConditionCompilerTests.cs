using System.Linq.Expressions;
using System.Text.Json.Nodes;
using FSH.Modules.AccessControl.Conditions;

namespace AccessControl.Tests;

public sealed class ConditionCompilerTests
{
    public enum DocStatus { Draft, Published }

    public sealed class Doc
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = "";
        public string? CreatedBy { get; init; }
        public Guid OwnerId { get; init; }
        public Guid? ReviewerId { get; init; }
        public int Views { get; init; }
        public decimal? Price { get; init; }
        public bool Pinned { get; init; }
        public DateTimeOffset CreatedOnUtc { get; init; }
        public DateTime? PublishedAt { get; init; }
        public DocStatus Status { get; init; }
        public DocStatus? PreviousStatus { get; init; }
        public string Role { get; init; } = "";
        public Doc? Parent { get; init; }
        public List<string> Tags { get; init; } = [];
    }

    private static readonly Guid Me = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly ConditionVariables Vars = new(Me, ["Editor", "Author"]);
    private static readonly DateTimeOffset Jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Doc[] Docs =
    [
        new() { Title = "a", CreatedBy = Me.ToString(), OwnerId = Me, ReviewerId = null, Views = 1, Price = null, Pinned = true,
                CreatedOnUtc = Jan1, PublishedAt = null, Status = DocStatus.Draft, PreviousStatus = null, Role = "Editor" },
        new() { Title = "b", CreatedBy = Other.ToString(), OwnerId = Other, ReviewerId = Me, Views = 5, Price = 9.5m, Pinned = false,
                CreatedOnUtc = Jan1.AddDays(10), PublishedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                Status = DocStatus.Published, PreviousStatus = DocStatus.Draft, Role = "Author" },
        new() { Title = "c", CreatedBy = null, OwnerId = Other, ReviewerId = Other, Views = 10, Price = 20m, Pinned = false,
                CreatedOnUtc = Jan1.AddDays(20), PublishedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                Status = DocStatus.Published, PreviousStatus = DocStatus.Published, Role = "Admin" },
    ];

    private static string[] Match(string json) =>
        Docs.AsQueryable().Where(ConditionCompiler.Build<Doc>(JsonNode.Parse(json)!.AsObject(), Vars))
            .Select(d => d.Title).ToArray();

    private static string? Problem(string json)
    {
        var x = Expression.Parameter(typeof(Doc), "x");
        var body = ConditionCompiler.TryBuild(json, x, Vars, out string? problem);
        if (body is not null) return null;
        problem.ShouldNotBeNullOrWhiteSpace();
        return problem;
    }

    #region Operators

    [Theory]
    [InlineData("""{"title": {"$eq": "a"}}""", "a")]
    [InlineData("""{"title": "a"}""", "a")]
    [InlineData("""{"TITLE": {"$ne": "a"}}""", "b,c")]
    [InlineData("""{"title": {"$in": ["a", "c", "zz"]}}""", "a,c")]
    [InlineData("""{"title": {"$nin": ["a", "c"]}}""", "b")]
    [InlineData("""{"views": {"$gt": 1}}""", "b,c")]
    [InlineData("""{"views": {"$gte": 5}}""", "b,c")]
    [InlineData("""{"views": {"$lt": 5}}""", "a")]
    [InlineData("""{"views": {"$lte": 5}}""", "a,b")]
    [InlineData("""{"views": {"$gt": 1, "$lt": 10}}""", "b")]
    [InlineData("""{"price": {"$gte": 9.5}}""", "b,c")]
    [InlineData("""{"pinned": true}""", "a")]
    [InlineData("""{"createdOnUtc": {"$gt": "2026-01-05T00:00:00Z"}}""", "b,c")]
    [InlineData("""{"createdOnUtc": {"$lt": "2026-01-05T07:00:00+07:00"}}""", "a")]
    [InlineData("""{"publishedAt": {"$lte": "2026-02-01T00:00:00Z"}}""", "b")]
    [InlineData("""{"status": "Published"}""", "b,c")]
    [InlineData("""{"status": {"$eq": 0}}""", "a")]
    [InlineData("""{"previousStatus": {"$in": ["Draft"]}}""", "b")]
    [InlineData("""{"id": {"$ne": "00000000-0000-0000-0000-000000000001"}}""", "a,b,c")]
    public void Build_Should_ApplyComparisonOperators(string json, string expected)
    {
        Match(json).ShouldBe(expected.Split(','));
    }

    [Theory]
    [InlineData("""{"reviewerId": {"$null": true}}""", "a")]
    [InlineData("""{"reviewerId": {"$null": false}}""", "b,c")]
    [InlineData("""{"createdBy": {"$null": true}}""", "c")]
    [InlineData("""{"price": {"$null": false}}""", "b,c")]
    [InlineData("""{"views": {"$null": true}}""", "")]
    [InlineData("""{"views": {"$null": false}}""", "a,b,c")]
    public void Build_Should_ApplyNull(string json, string expected)
    {
        Match(json).ShouldBe(expected.Length == 0 ? [] : expected.Split(','));
    }

    [Theory]
    [InlineData("""{"$and": [{"views": {"$gt": 1}}, {"title": "c"}]}""", "c")]
    [InlineData("""{"$or": [{"title": "a"}, {"title": "c"}]}""", "a,c")]
    [InlineData("""{"$not": {"title": "a"}}""", "b,c")]
    [InlineData("""{"$or": [{"$not": {"views": {"$gte": 5}}}, {"$and": [{"pinned": false}, {"price": {"$gt": 10}}]}]}""", "a,c")]
    [InlineData("""{"title": {"$ne": "a"}, "views": {"$lt": 10}}""", "b")]
    [InlineData("""{}""", "a,b,c")]
    public void Build_Should_CombineLogicalOperators(string json, string expected)
    {
        Match(json).ShouldBe(expected.Split(','));
    }

    #endregion

    #region Variables

    [Theory]
    // CreatedBy is a string written by the audit interceptor as Guid.ToString() ("D"); OwnerId/ReviewerId are Guids.
    [InlineData("""{"createdBy": {"$eq": "$CURRENT_USER"}}""", "a")]
    [InlineData("""{"ownerId": "$CURRENT_USER"}""", "a")]
    [InlineData("""{"ownerId": {"$ne": "$CURRENT_USER"}}""", "b,c")]
    [InlineData("""{"reviewerId": {"$in": ["$CURRENT_USER", "33333333-3333-3333-3333-333333333333"]}}""", "b")]
    [InlineData("""{"role": {"$eq": "$CURRENT_ROLES"}}""", "a,b")]
    [InlineData("""{"role": {"$in": "$CURRENT_ROLES"}}""", "a,b")]
    [InlineData("""{"role": {"$ne": "$CURRENT_ROLES"}}""", "c")]
    [InlineData("""{"role": {"$nin": "$CURRENT_ROLES"}}""", "c")]
    public void Build_Should_ResolveVariables(string json, string expected)
    {
        Match(json).ShouldBe(expected.Split(','));
    }

    [Fact]
    public void Build_Should_MatchNothingForRoles_When_UserHasNoRoles()
    {
        var predicate = ConditionCompiler.Build<Doc>(JsonNode.Parse("""{"role": {"$in": "$CURRENT_ROLES"}}""")!.AsObject(),
            new ConditionVariables(Me, []));

        Docs.AsQueryable().Where(predicate).ShouldBeEmpty();
    }

    [Fact]
    public void Build_Should_TreatRoleNamesAsData_When_RoleLooksLikeVariable()
    {
        var predicate = ConditionCompiler.Build<Doc>(JsonNode.Parse("""{"title": {"$in": "$CURRENT_ROLES"}}""")!.AsObject(),
            new ConditionVariables(Me, ["$CURRENT_USER", "b"]));

        Docs.AsQueryable().Where(predicate).Select(d => d.Title).ShouldBe(["b"]);
    }

    [Fact]
    public void Build_Should_PassValuesAsCapturedFields_NotInlineConstants()
    {
        // EF Core turns a member access on a captured object into a SQL parameter (one cached plan per shape).
        var predicate = ConditionCompiler.Build<Doc>(JsonNode.Parse("""{"title": "a", "views": {"$in": [1, 2]}}""")!.AsObject(), Vars);

        string text = predicate.Body.ToString();
        text.ShouldNotContain("\"a\"");
        text.ShouldContain(".Value");
    }

    #endregion

    #region Fail closed

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("""{"nope": 1}""")]
    [InlineData("""{"parent": {"$null": true}}""")]
    [InlineData("""{"tags": {"$null": true}}""")]
    [InlineData("""{"views": "five"}""")]
    [InlineData("""{"views": 1.5}""")]
    [InlineData("""{"title": 5}""")]
    [InlineData("""{"ownerId": "not-a-guid"}""")]
    [InlineData("""{"pinned": "true"}""")]
    [InlineData("""{"status": "Archived"}""")]
    [InlineData("""{"publishedAt": {"$gt": "2026-01-01T00:00:00"}}""")]
    [InlineData("""{"title": {"$gt": "a"}}""")]
    [InlineData("""{"status": {"$gt": 0}}""")]
    [InlineData("""{"ownerId": {"$lt": "$CURRENT_USER"}}""")]
    [InlineData("""{"views": "$CURRENT_USER"}""")]
    [InlineData("""{"ownerId": {"$in": "$CURRENT_ROLES"}}""")]
    [InlineData("""{"role": {"$gt": "$CURRENT_ROLES"}}""")]
    [InlineData("""{"role": "$NOW"}""")]
    [InlineData("""{"role": {"$in": ["$CURRENT_ROLES"]}}""")]
    [InlineData("""{"title": {"$like": "a"}}""")]
    [InlineData("""{"title": {}}""")]
    [InlineData("""{"title": null}""")]
    [InlineData("""{"title": ["a"]}""")]
    [InlineData("""{"title": {"$in": []}}""")]
    [InlineData("""{"title": {"$in": "a"}}""")]
    [InlineData("""{"title": {"$null": 1}}""")]
    [InlineData("""{"$and": []}""")]
    [InlineData("""{"$or": {"title": "a"}}""")]
    [InlineData("""{"$and": ["x"]}""")]
    [InlineData("""{"$not": [{"title": "a"}]}""")]
    [InlineData("""{"$xor": []}""")]
    [InlineData("""{"title.length": 1}""")]
    public void TryBuild_Should_FailClosed_When_ConditionIsUnusable(string json)
    {
        Problem(json).ShouldNotBeNull();
    }

    [Fact]
    public void TryBuild_Should_FailClosed_When_OverNodeLimit()
    {
        var or = new JsonArray(Enumerable.Range(0, 201).Select(i => (JsonNode?)new JsonObject { ["views"] = i }).ToArray());
        Problem(new JsonObject { ["$or"] = or }.ToJsonString()).ShouldNotBeNull();
    }

    [Fact]
    public void TryBuild_Should_FailClosed_When_OverDepthLimit()
    {
        JsonObject condition = new() { ["views"] = 1 };
        for (int i = 0; i < 9; i++) condition = new JsonObject { ["$not"] = condition };
        Problem(condition.ToJsonString()).ShouldNotBeNull();

        JsonObject atLimit = new() { ["views"] = 1 };
        for (int i = 0; i < 8; i++) atLimit = new JsonObject { ["$not"] = atLimit };
        Problem(atLimit.ToJsonString()).ShouldBeNull();
    }

    [Fact]
    public void TryBuild_Should_FailClosed_When_ListOverLimit()
    {
        var values = new JsonArray(Enumerable.Range(0, 101).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        Problem(new JsonObject { ["views"] = new JsonObject { ["$in"] = values } }.ToJsonString()).ShouldNotBeNull();
    }

    [Fact]
    public void AnyOf_Should_MatchNothing_When_NoConditions()
    {
        var x = Expression.Parameter(typeof(Doc), "x");
        var predicate = Expression.Lambda<Func<Doc, bool>>(ConditionCompiler.AnyOf([]), x);

        Docs.AsQueryable().Where(predicate).ShouldBeEmpty();
    }

    #endregion

    #region Save-time validation

    [Theory]
    [InlineData("""{"anyProperty": {"$eq": "$CURRENT_USER"}}""")]
    [InlineData("""{"role": {"$in": "$CURRENT_ROLES"}, "$or": [{"a": 1}, {"$not": {"b": {"$null": true}}}]}""")]
    [InlineData("""{"n": {"$gt": 1, "$lte": 2.5}, "s": {"$nin": ["x", true, 3]}}""")]
    public void Validate_Should_Accept_When_StructureIsValid(string json)
    {
        ConditionCompiler.Validate(JsonNode.Parse(json)!.AsObject()).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"a": {"$like": "x"}}""")]
    [InlineData("""{"a": {"$in": []}}""")]
    [InlineData("""{"a": {"$gt": "$CURRENT_ROLES"}}""")]
    [InlineData("""{"a": "$CURRENT_USERS"}""")]
    [InlineData("""{"a b": 1}""")]
    [InlineData("""{"a": {"$null": "yes"}}""")]
    [InlineData("""{"a": {"b": 1}}""")]
    [InlineData("""{"$or": []}""")]
    [InlineData("""{"$where": "1=1"}""")]
    public void Validate_Should_Reject_When_StructureIsInvalid(string json)
    {
        ConditionCompiler.Validate(JsonNode.Parse(json)!.AsObject()).ShouldNotBeNull();
    }

    #endregion
}
