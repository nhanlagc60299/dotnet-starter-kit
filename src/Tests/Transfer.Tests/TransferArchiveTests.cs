using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Transfer;
using FSH.Modules.Transfer.Archive;
using FSH.Modules.Transfer.Features.v1.Import.CreateImport;

namespace Transfer.Tests;

public sealed class TransferArchiveTests
{
    private static readonly TransferOptions Defaults = new();

    #region Happy Path

    [Fact]
    public async Task WriteThenOpen_Should_RoundTripRowsAndManifest()
    {
        var created = new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);
        using var stream = new MemoryStream();
        var written = await TransferArchiveWriter.WriteAsync(stream,
            [
                ("catalog.brands", Rows(new JsonObject { ["name"] = "Acme", ["description"] = "Đồ gia dụng" }, new JsonObject { ["name"] = "Zeta" })),
                ("catalog.empty", Rows()),
            ],
            created, Defaults.MaxLineBytes);

        using var archive = TransferArchive.Open(stream, Defaults);

        archive.Manifest.Format.ShouldBe("fsh.transfer/1");
        archive.Manifest.CreatedAtUtc.ShouldBe(created);
        archive.Manifest.Files.Select(f => (f.Path, f.EntityType, f.Rows))
            .ShouldBe([("data/catalog.brands.ndjson", "catalog.brands", 2), ("data/catalog.empty.ndjson", "catalog.empty", 0)]);
        archive.Manifest.Files[0].Sha256.ShouldBe(written.Files[0].Sha256);

        var rows = await ToListAsync(archive.ReadRowsAsync("catalog.brands"));
        rows.Select(r => r["name"]!.GetValue<string>()).ShouldBe(["Acme", "Zeta"]);
        rows[0]["description"]!.GetValue<string>().ShouldBe("Đồ gia dụng");
        (await ToListAsync(archive.ReadRowsAsync("catalog.empty"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_Should_Throw_When_RowIsLongerThanLimit()
    {
        using var stream = new MemoryStream();
        await Should.ThrowAsync<InvalidOperationException>(() => TransferArchiveWriter.WriteAsync(stream,
            [("catalog.brands", Rows(new JsonObject { ["name"] = new string('a', 2000) }))], DateTime.UtcNow, maxLineBytes: 1024));
    }

    #endregion

    #region Exception

    [Fact]
    public void Open_Should_Reject_When_ShaDoesNotMatchManifest()
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"name\":\"Acme\"}\n");
        using var stream = Zip(
            ("manifest.json", Manifest(("catalog.brands", 1, Sha(Encoding.UTF8.GetBytes("{\"name\":\"Other\"}\n"))))),
            ("data/catalog.brands.ndjson", data));

        ShouldBeInvalid(stream, Defaults, "SHA-256");
    }

    [Theory]
    [InlineData("../evil.ndjson")]
    [InlineData("data/../../evil.ndjson")]
    [InlineData("/etc/passwd")]
    [InlineData("data\\evil.ndjson")]
    [InlineData("C:evil.ndjson")]
    [InlineData("data/./x.ndjson")]
    public void Open_Should_Reject_ZipSlipEntryNames(string name)
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"name\":\"Acme\"}\n");
        using var stream = Zip(
            ("manifest.json", Manifest(("catalog.brands", 1, Sha(data)))),
            ("data/catalog.brands.ndjson", data),
            (name, data));

        ShouldBeInvalid(stream, Defaults, "not allowed");
    }

    [Fact]
    public void Open_Should_Reject_When_EntryCountExceedsLimit()
    {
        byte[] data = Encoding.UTF8.GetBytes("{}\n");
        using var stream = Zip(("manifest.json", data), ("a.ndjson", data), ("b.ndjson", data));

        ShouldBeInvalid(stream, new TransferOptions { MaxEntries = 2 }, "more than 2 entries");
    }

    [Fact]
    public void Open_Should_Reject_When_UncompressedSizeExceedsLimit()
    {
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"name\":\"Acme\"}\n", 200)));
        using var stream = Zip(
            ("manifest.json", Manifest(("catalog.brands", 200, Sha(data)))),
            ("data/catalog.brands.ndjson", data));

        ShouldBeInvalid(stream, new TransferOptions { MaxUncompressedBytes = 2048 }, "inflates to more than");
    }

    [Fact]
    public void Open_Should_Reject_When_CompressionRatioExceedsLimit()
    {
        // 4 MiB of one repeated row deflates far beyond 100:1.
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"name\":\"aaaaaaaaaaaaaaa\"}\n", 4 * 1024 * 1024 / 26)));
        using var stream = Zip(
            ("manifest.json", Manifest(("catalog.brands", 4 * 1024 * 1024 / 26, Sha(data)))),
            ("data/catalog.brands.ndjson", data));

        ShouldBeInvalid(stream, Defaults, "compression ratio");
    }

    [Fact]
    public void Open_Should_Reject_When_LineIsTooLong()
    {
        byte[] data = Encoding.UTF8.GetBytes($"{{\"name\":\"{new string('a', 2000)}\"}}\n");
        using var stream = Zip(
            ("manifest.json", Manifest(("catalog.brands", 1, Sha(data)))),
            ("data/catalog.brands.ndjson", data));

        ShouldBeInvalid(stream, new TransferOptions { MaxLineBytes = 1024 }, "longer than 1024 bytes");
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("{\"name\":\"A\",\"name\":\"B\"}")]
    [InlineData("{\"name\":")]
    public void Open_Should_Reject_When_LineIsNotAJsonObject(string second)
    {
        byte[] data = Encoding.UTF8.GetBytes($"{{\"name\":\"Acme\"}}\n{second}\n");
        using var stream = Zip(
            ("manifest.json", Manifest(("catalog.brands", 2, Sha(data)))),
            ("data/catalog.brands.ndjson", data));

        ShouldBeInvalid(stream, Defaults, "Line 2");
    }

    [Fact]
    public void Open_Should_Reject_When_RowCountDiffersFromManifest()
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"name\":\"Acme\"}\n");
        using var stream = Zip(
            ("manifest.json", Manifest(("catalog.brands", 3, Sha(data)))),
            ("data/catalog.brands.ndjson", data));

        ShouldBeInvalid(stream, Defaults, "manifest.json says 3");
    }

    [Fact]
    public void Open_Should_Reject_EntriesNotListedInManifest()
    {
        byte[] data = Encoding.UTF8.GetBytes("{\"name\":\"Acme\"}\n");
        using var stream = Zip(
            ("manifest.json", Manifest(("catalog.brands", 1, Sha(data)))),
            ("data/catalog.brands.ndjson", data),
            ("data/extra.ndjson", data));

        ShouldBeInvalid(stream, Defaults, "not listed");
    }

    [Fact]
    public void Open_Should_Reject_WrongFormatAndBadEntityTypes()
    {
        byte[] data = Encoding.UTF8.GetBytes("{}\n");
        using (var wrongFormat = Zip(("manifest.json", Manifest("aicms.content", ("catalog.brands", 1, Sha(data)))), ("data/catalog.brands.ndjson", data)))
        {
            ShouldBeInvalid(wrongFormat, Defaults, "is not fsh.transfer/1");
        }

        using var badType = Zip(("manifest.json", Manifest(("Catalog Brands", 1, Sha(data)))), ("data/Catalog Brands.ndjson", data));
        ShouldBeInvalid(badType, Defaults, "is not valid");
    }

    [Fact]
    public void Open_Should_Reject_When_NotAZip()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 100)));
        ShouldBeInvalid(stream, Defaults, "not a valid zip");
    }

    #endregion

    #region Edge Cases

    [Theory]
    [InlineData("export.zip", "export.zip")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\bản xuất.zip", "b_n_xu_t.zip")]
    [InlineData("\"; filename=evil.exe", "filename_evil.exe")]
    [InlineData("...", "import.zip")]
    public void SafeFileName_Should_KeepOnlyTheLastSegmentAndSafeCharacters(string input, string expected)
    {
        CreateImportCommandHandler.SafeFileName(input).ShouldBe(expected);
    }

    #endregion

    private static async IAsyncEnumerable<JsonObject> Rows(params JsonObject[] rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    private static async Task<List<JsonObject>> ToListAsync(IAsyncEnumerable<JsonObject> rows)
    {
        var list = new List<JsonObject>();
        await foreach (var row in rows) list.Add(row);
        return list;
    }

    private static void ShouldBeInvalid(Stream stream, TransferOptions options, string messagePart)
    {
        var ex = Should.Throw<CustomException>(() => TransferArchive.Open(stream, options).Dispose());
        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.Message.ShouldContain(messagePart);
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Manifest(params (string EntityType, int Rows, string Sha)[] files) => Manifest("fsh.transfer/1", files);

    private static byte[] Manifest(string format, params (string EntityType, int Rows, string Sha)[] files)
    {
        var list = new JsonArray();
        foreach (var (type, rows, sha) in files)
        {
            list.Add(new JsonObject { ["path"] = $"data/{type}.ndjson", ["entityType"] = type, ["rows"] = rows, ["sha256"] = sha });
        }

        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = format,
            ["createdAtUtc"] = "2026-11-02T00:00:00Z",
            ["files"] = list,
        }.ToJsonString());
    }

    private static MemoryStream Zip(params (string Name, byte[] Data)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(data);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
