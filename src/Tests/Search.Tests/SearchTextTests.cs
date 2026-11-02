using FSH.Modules.Search.Features.v1.Documents.SearchDocuments;

namespace Search.Tests;

public sealed class SearchTextTests
{
    [Fact]
    public void ToTsQuery_Should_FoldAccents_And_PrefixTheLastWord() =>
        SearchText.Parse("học sinh").ToTsQuery().ShouldBe("(hoc & sinh:*)");

    [Fact]
    public void ToTsQuery_Should_NotPrefix_When_TextEndsWithSpace() =>
        SearchText.Parse("học sinh ").ToTsQuery().ShouldBe("(hoc & sinh)");

    [Fact]
    public void ToTsQuery_Should_NotPrefix_SingleLetter() =>
        SearchText.Parse("m").ToTsQuery().ShouldNotContain(":*");

    [Fact]
    public void ToTsQuery_Should_MapPhraseNegationAndOr() =>
        SearchText.Parse("Học \"sinh giỏi\" -lớp OR trường").ToTsQuery()
            .ShouldBe("(hoc & (sinh <-> gioi) & !lop) | truong:*");

    [Fact]
    public void ToTsQuery_Should_DropGroupOfOnlyNegations()
    {
        var q = SearchText.Parse("zebra OR -dien");
        q.ToTsQuery().ShouldNotContain("!");
        q.ToTsQuery().ShouldContain("zebra");
        SearchText.Parse("zebra -dien").ToTsQuery().ShouldContain("!"); // AND NOT still works
    }

    [Fact]
    public void ToTsQuery_Should_NeverPassEngineSyntaxThrough()
    {
        string ts = SearchText.Parse("a\" OR 1=1 -- NEAR(x) title:* ); DROP & | ! <->").ToTsQuery();
        ts.ShouldNotContain(";");
        ts.ShouldNotContain("'");
        ts.ShouldMatch(@"^[a-z0-9 ()&|!<>:*-]*$");
    }

    [Fact]
    public void PhraseTsQuery_Should_ChainBareTokens() =>
        SearchText.Parse("học sinh").PhraseTsQuery().ShouldBe("hoc <-> sinh:*");

    [Fact]
    public void PhraseTsQuery_Should_BeNull_When_QueryHasOperators() =>
        SearchText.Parse("hoc -sinh").PhraseTsQuery().ShouldBeNull();

    [Theory]
    [InlineData("-only")]
    [InlineData("!!! ???")]
    [InlineData("")]
    public void IsEmpty_Should_BeTrue_When_NothingPositiveToMatch(string raw) =>
        SearchText.Parse(raw).IsEmpty.ShouldBeTrue();

    [Fact]
    public void Tokens_Should_FoldDStroke() =>
        SearchText.Tokens("Đồng").ShouldHaveSingleItem().ShouldBe("dong");

    [Fact]
    public void Parse_Should_CapTokenCount()
    {
        string raw = string.Join(' ', Enumerable.Range(0, 50).Select(i => $"w{i}"));
        SearchText.Parse(raw).Groups.Sum(g => g.Count).ShouldBe(SearchText.MaxTokens);
    }
}
