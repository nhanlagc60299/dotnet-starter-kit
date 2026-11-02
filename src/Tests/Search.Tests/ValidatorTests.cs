using FSH.Modules.Search.Contracts.v1.Documents;
using FSH.Modules.Search.Features.v1.Documents.IndexSearchDocument;
using FSH.Modules.Search.Features.v1.Documents.RemoveSearchDocument;
using FSH.Modules.Search.Features.v1.Documents.SearchDocuments;

namespace Search.Tests;

public sealed class ValidatorTests
{
    [Fact]
    public void IndexValidator_Should_Pass_When_CommandIsValid() =>
        new IndexSearchDocumentCommandValidator()
            .Validate(new IndexSearchDocumentCommand("Product", "42", "Học sinh giỏi", "tóm tắt", "nội dung", "/p/42"))
            .IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("", "42", "Title")]
    [InlineData("Product", "", "Title")]
    [InlineData("Product", "42", "")]
    public void IndexValidator_Should_Fail_When_RequiredFieldMissing(string type, string id, string title) =>
        new IndexSearchDocumentCommandValidator()
            .Validate(new IndexSearchDocumentCommand(type, id, title))
            .IsValid.ShouldBeFalse();

    [Fact]
    public void IndexValidator_Should_Fail_When_FieldsTooLong()
    {
        var v = new IndexSearchDocumentCommandValidator();
        v.Validate(new IndexSearchDocumentCommand(new string('t', 101), "1", "x")).IsValid.ShouldBeFalse();
        v.Validate(new IndexSearchDocumentCommand("t", new string('i', 101), "x")).IsValid.ShouldBeFalse();
        v.Validate(new IndexSearchDocumentCommand("t", "1", new string('x', 501))).IsValid.ShouldBeFalse();
        v.Validate(new IndexSearchDocumentCommand("t", "1", "x", Url: new string('u', 1001))).IsValid.ShouldBeFalse();
        v.Validate(new IndexSearchDocumentCommand("t", "1", "x", Body: new string('b', IndexSearchDocumentCommandValidator.MaxBodyLength + 1))).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/products/42")]
    [InlineData("/")]
    [InlineData("http://example.com/a")]
    [InlineData("https://example.com/a?b=c#d")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void IndexValidator_Should_Accept_RelativeOrHttpUrl(string? url) =>
        new IndexSearchDocumentCommandValidator()
            .Validate(new IndexSearchDocumentCommand("t", "1", "x", Url: url))
            .IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("//evil.example.com/x")]
    [InlineData("/\\evil.example.com/x")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("products/42")]
    [InlineData("")]
    public void IndexValidator_Should_Reject_UnsafeOrNonHttpUrl(string url) =>
        new IndexSearchDocumentCommandValidator()
            .Validate(new IndexSearchDocumentCommand("t", "1", "x", Url: url))
            .IsValid.ShouldBeFalse();

    // Browsers strip tab/CR/LF while parsing a URL, so these become "//evil.com" (an open redirect).
    [Theory]
    [InlineData("/\t/evil.com")]
    [InlineData("/\n/evil.com")]
    [InlineData("/\r/evil.com")]
    [InlineData("/ /evil.com")]
    [InlineData("/\u000B/evil.com")]
    [InlineData("/\u00A0/evil.com")]
    [InlineData("/products/42\n")]
    [InlineData("https://example.com/\t")]
    public void IndexValidator_Should_Reject_ControlOrWhitespace_When_BrowserWouldStripIt(string url) =>
        new IndexSearchDocumentCommandValidator()
            .Validate(new IndexSearchDocumentCommand("t", "1", "x", Url: url))
            .IsValid.ShouldBeFalse();

    // Browsers do not percent-decode into the authority, so "/%09/evil.com" stays a same-origin path.
    [Theory]
    [InlineData("/%09/evil.com")]
    [InlineData("/%2F/evil.com")]
    [InlineData("/search?q=a%20b")]
    public void IndexValidator_Should_Accept_PercentEncodedRelativeUrl_Because_BrowsersKeepItAPath(string url) =>
        new IndexSearchDocumentCommandValidator()
            .Validate(new IndexSearchDocumentCommand("t", "1", "x", Url: url))
            .IsValid.ShouldBeTrue();

    [Fact]
    public void RemoveValidator_Should_Fail_When_KeyMissing()
    {
        var v = new RemoveSearchDocumentCommandValidator();
        v.Validate(new RemoveSearchDocumentCommand("Product", "42")).IsValid.ShouldBeTrue();
        v.Validate(new RemoveSearchDocumentCommand("", "42")).IsValid.ShouldBeFalse();
        v.Validate(new RemoveSearchDocumentCommand("Product", "")).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("hoc sinh", 1, 20, true)]
    [InlineData("", 1, 20, false)]
    [InlineData("-only", 1, 20, false)]
    [InlineData("!!!", 1, 20, false)]
    [InlineData("hoc", 0, 20, false)]
    [InlineData("hoc", 1, 0, false)]
    [InlineData("hoc", 1, 101, false)]
    public void SearchValidator_Should_CheckTextAndPaging(string q, int page, int size, bool valid) =>
        new SearchDocumentsQueryValidator()
            .Validate(new SearchDocumentsQuery(q, null, page, size))
            .IsValid.ShouldBe(valid);

    [Fact]
    public void SearchValidator_Should_Fail_When_TextTooLong() =>
        new SearchDocumentsQueryValidator()
            .Validate(new SearchDocumentsQuery(new string('a', SearchText.MaxLength + 1)))
            .IsValid.ShouldBeFalse();
}
