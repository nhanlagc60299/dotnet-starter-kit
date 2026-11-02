using System.Text;
using FSH.Modules.Search.Contracts.Text;

namespace Search.Tests;

public sealed class TextTests
{
    [Theory]
    [InlineData("Xin chào Việt Nam!", "xin-chao-viet-nam")]
    [InlineData("  Đường đến thành công  ", "duong-den-thanh-cong")]
    [InlineData("ASP.NET Core & LangGraph", "asp-net-core-langgraph")]
    [InlineData("---", "")]
    [InlineData(null, "")]
    public void Generate_Should_ReturnUrlFriendlySlug(string? input, string expected) =>
        SlugHelper.Generate(input).ShouldBe(expected);

    [Theory]
    [InlineData("Học Sinh Giỏi", "hoc sinh gioi")]
    [InlineData("Đà Nẵng", "da nang")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_Should_LowercaseAndStripAccents(string? input, string expected) =>
        TextNormalizer.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Normalize_Should_TreatComposedAndDecomposedInputAlike() =>
        TextNormalizer.Normalize("Nẵng".Normalize(NormalizationForm.FormD)).ShouldBe(TextNormalizer.Normalize("Nẵng"));

    [Fact]
    public void SafeNormalize_Should_DropUnpairedSurrogates_When_NormalizeWouldThrow() =>
        "a\uD800b".SafeNormalize(NormalizationForm.FormC).ShouldBe("ab");
}
