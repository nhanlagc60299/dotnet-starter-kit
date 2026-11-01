using System.Text.Json;
using System.Text.Json.Nodes;
using FSH.Modules.AccessControl.Contracts.Dtos;
using FSH.Modules.AccessControl.Contracts.v1.Grants;
using FSH.Modules.AccessControl.Features.v1.Grants.UpdateRoleGrants;
using FSH.Modules.AccessControl.Services;

namespace AccessControl.Tests;

public sealed class GrantRulesTests
{
    #region Subjects and coverage

    [Theory]
    [InlineData("tickets", "tickets", true)]
    [InlineData("tickets", "tickets:open", false)]
    [InlineData("content:*", "content:article", true)]
    [InlineData("content:*", "content:*", true)]
    [InlineData("content:*", "contents:article", false)]
    [InlineData("content:*", "content", false)]
    [InlineData("content:article", "content:*", false)]
    public void SubjectCovers_Should_MatchExactOrWildcard(string granted, string requested, bool expected)
    {
        AccessRules.SubjectCovers(granted, requested).ShouldBe(expected);
    }

    [Fact]
    public void Changed_Should_IgnoreJsonKeyOrderAndSpacing()
    {
        CachedGrant[] before = [new("tickets", "read", """{"a": 1, "b": {"$in": [1, 2]}}""")];
        CachedGrant[] after = [new("tickets", "read", """{"b":{"$in":[1,2]},"a":1}""")];

        AccessRules.Changed(before, after).ShouldBeEmpty();
    }

    [Fact]
    public void Changed_Should_ListAddedRemovedAndModifiedGrants()
    {
        CachedGrant[] before = [new("tickets", "read", null), new("tickets", "update", null), new("files", "read", null)];
        CachedGrant[] after = [new("tickets", "read", null), new("tickets", "update", """{"a": 1}"""), new("chat", "read", null)];

        AccessRules.Changed(before, after).Select(g => $"{g.Subject}/{g.Action}/{g.Condition is null}")
            .ShouldBe(["tickets/update/False", "chat/read/True", "tickets/update/True", "files/read/True"], ignoreOrder: true);
    }

    [Fact]
    public void FirstUncovered_Should_RequireUnconditionalGrantOnSameAction()
    {
        CachedGrant[] mine = [new("tickets", "read", null), new("content:*", "update", null), new("files", "read", """{"a": 1}""")];

        AccessRules.FirstUncovered(mine, [new("tickets", "read", """{"x": 1}""")]).ShouldBeNull();
        AccessRules.FirstUncovered(mine, [new("content:article", "update", null)]).ShouldBeNull();
        AccessRules.FirstUncovered(mine, [new("tickets", "update", null)]).ShouldNotBeNull();
        AccessRules.FirstUncovered(mine, [new("files", "read", """{"a": 1}""")]).ShouldNotBeNull();
        AccessRules.FirstUncovered([], [new("tickets", "read", null)]).ShouldNotBeNull();
    }

    #endregion

    #region Validator

    private static readonly UpdateRoleGrantsCommandValidator Validator = new();

    private static bool IsValid(params AccessGrantDto[] grants) =>
        Validator.Validate(new UpdateRoleGrantsCommand("Editor", grants)).IsValid;

    [Theory]
    [InlineData("tickets", "read", true)]
    [InlineData("content:article", "readDraft", true)]
    [InlineData("content:*", "update", true)]
    [InlineData("preview-links", "create", true)]
    [InlineData("Tickets", "read", false)]
    [InlineData("tickets\n", "read", false)]
    [InlineData("tickets", "read\n", false)]
    [InlineData("1tickets", "read", false)]
    [InlineData("tickets:", "read", false)]
    [InlineData("a:b:c", "read", false)]
    [InlineData("<script>", "read", false)]
    [InlineData("tickets", "Read", false)]
    [InlineData("tickets", "read-all", false)]
    [InlineData("", "read", false)]
    [InlineData("tickets", "", false)]
    public void Validator_Should_CheckSubjectAndAction(string subject, string action, bool expected)
    {
        IsValid(new AccessGrantDto(subject, action)).ShouldBe(expected);
    }

    [Fact]
    public void Validator_Should_Reject_When_LengthsExceeded()
    {
        IsValid(new AccessGrantDto("a" + new string('b', 100), "read")).ShouldBeFalse();
        IsValid(new AccessGrantDto("tickets", "a" + new string('b', 50))).ShouldBeFalse();
        Validator.Validate(new UpdateRoleGrantsCommand(new string('r', 257), [])).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validator_Should_Reject_When_SubjectActionRepeated()
    {
        IsValid(new AccessGrantDto("tickets", "read"), new AccessGrantDto("tickets", "read", new JsonObject { ["a"] = 1 }))
            .ShouldBeFalse();
        IsValid(new AccessGrantDto("tickets", "read"), new AccessGrantDto("tickets", "update")).ShouldBeTrue();
    }

    [Fact]
    public void Validator_Should_Reject_When_TooManyGrants()
    {
        var grants = Enumerable.Range(0, 501).Select(i => new AccessGrantDto($"s{i}", "read")).ToArray();
        IsValid(grants).ShouldBeFalse();
        IsValid(grants[..500]).ShouldBeTrue();
    }

    [Fact]
    public void Validator_Should_CheckConditions()
    {
        IsValid(new AccessGrantDto("tickets", "read", (JsonObject)JsonNode.Parse("""{"createdBy": "$CURRENT_USER"}""")!)).ShouldBeTrue();
        IsValid(new AccessGrantDto("tickets", "read", new JsonObject())).ShouldBeTrue();
        IsValid(new AccessGrantDto("tickets", "read", (JsonObject)JsonNode.Parse("""{"a": {"$regex": ".*"}}""")!)).ShouldBeFalse();
        IsValid(new AccessGrantDto("tickets", "read", new JsonObject { ["a"] = new string('x', 8_001) })).ShouldBeFalse();
    }

    [Fact]
    public void Validator_Should_Reject_When_ConditionRepeatsAPropertyName()
    {
        var grants = JsonSerializer.Deserialize<List<AccessGrantDto>>(
            """[{"subject": "tickets", "action": "read", "condition": {"a": 1, "a": 2}}]""", JsonSerializerOptions.Web)!;

        Validator.Validate(new UpdateRoleGrantsCommand("Editor", grants)).Errors
            .ShouldHaveSingleItem().ErrorMessage.ShouldBe("The condition repeats a property name.");
    }

    #endregion
}
