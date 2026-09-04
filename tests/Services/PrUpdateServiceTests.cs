using System;
using System.Linq;
using KalOS.Services;

namespace KalOS.Tests.Services;

public class PrUpdateServiceTests
{
    // ── ParsePullRequests ─────────────────────────────────────────────────

    [Fact]
    public void ParsePullRequests_ParsesFieldsAndSortsNewestFirst()
    {
        string json = """
        [
          {
            "number": 7,
            "title": "Tweak MMCSS latency",
            "user": { "login": "contributor-one" },
            "state": "open",
            "html_url": "https://github.com/1k09-byte/KalOSTOOLKIT/pull/7",
            "head": { "ref": "mmcss", "sha": "abc123def456" }
          },
          {
            "number": 12,
            "title": "Add a tweak",
            "user": { "login": "contributor-two" },
            "html_url": "https://github.com/1k09-byte/KalOSTOOLKIT/pull/12",
            "head": { "ref": "tweak", "sha": "fff000aaa111" }
          }
        ]
        """;

        var prs = PrUpdateService.ParsePullRequests(json);

        Assert.Equal(2, prs.Count);
        Assert.Equal(12, prs[0].Number);          // newest (highest number) first
        Assert.Equal(7, prs[1].Number);
        Assert.Equal("contributor-two", prs[0].Author);
        Assert.Equal("tweak", prs[0].HeadRef);
        Assert.Equal("fff000aaa111", prs[0].HeadSha);
        Assert.Equal("https://github.com/1k09-byte/KalOSTOOLKIT/pull/12", prs[0].HtmlUrl);
        Assert.Equal("#12 — Add a tweak", prs[0].Label);
    }

    [Fact]
    public void ParsePullRequests_SkipsEntriesWithoutHeadSha()
    {
        string json = """[ { "number": 5, "title": "no head" } ]""";
        Assert.Empty(PrUpdateService.ParsePullRequests(json));
    }

    [Fact]
    public void ParsePullRequests_ReturnsEmptyForNonArrayOrInvalidPayload()
    {
        Assert.Empty(PrUpdateService.ParsePullRequests("{}"));
        Assert.Empty(PrUpdateService.ParsePullRequests("not json"));
        Assert.Empty(PrUpdateService.ParsePullRequests(""));
    }

    // ── ParseChangedFiles ─────────────────────────────────────────────────

    [Fact]
    public void ParseChangedFiles_ParsesFileEntries()
    {
        string json = """
        [
          { "filename": "os-changes.json", "status": "modified", "additions": 4, "deletions": 1 },
          { "filename": "README.md", "status": "modified", "additions": 10, "deletions": 2 },
          { "filename": "Services/NewService.cs", "status": "added", "additions": 120, "deletions": 0 }
        ]
        """;

        var files = PrUpdateService.ParseChangedFiles(json);

        Assert.Equal(3, files.Count);
        Assert.Equal("os-changes.json", files[0].Filename);
        Assert.Equal("modified", files[0].Status);
        Assert.Equal(4, files[0].Additions);
        Assert.Equal(1, files[0].Deletions);
        Assert.Equal("os-changes.json (+4 -1)", files[0].Summary);
    }

    [Fact]
    public void ParseChangedFiles_SkipsEntriesWithoutFilename()
    {
        string json = """[ { "status": "modified" } ]""";
        Assert.Empty(PrUpdateService.ParseChangedFiles(json));
    }

    // ── Merge: path safety + journal round-trip + renames ─────────────────

    [Theory]
    [InlineData("Services/Foo.cs", true)]
    [InlineData("os-changes.json", true)]
    [InlineData("", false)]
    [InlineData("../escape.cs", false)]
    [InlineData("a/../../escape.cs", false)]
    [InlineData("C:/abs/path.cs", false)]
    public void TryResolveRepoPath_AllowsOnlyPathsInsideTheRepo(string relative, bool expected)
    {
        string repoRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kalos-pr-tests");

        bool ok = PrUpdateService.TryResolveRepoPath(repoRoot, relative, out string fullPath);

        Assert.Equal(expected, ok);
        if (ok)
        {
            Assert.StartsWith(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(repoRoot)),
                System.IO.Path.GetFullPath(fullPath));
        }
    }

    [Fact]
    public void MergeJournal_RoundTripsThroughJsonWithStringEnums()
    {
        var journal = new PrUpdateService.MergeJournal
        {
            Overwritten = { "os-changes.json" },
            Added = { "Services/New.cs" },
            Deleted = { "old.cs" },
            RenamedFrom = { "Services/Old.cs" }
        };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var restored = System.Text.Json.JsonSerializer.Deserialize<PrUpdateService.MergeJournal>(
            System.Text.Json.JsonSerializer.Serialize(journal, options), options);

        Assert.NotNull(restored);
        Assert.Equal(journal.Overwritten, restored!.Overwritten);
        Assert.Equal(journal.Added, restored.Added);
        Assert.Equal(journal.Deleted, restored.Deleted);
        Assert.Equal(journal.RenamedFrom, restored.RenamedFrom);
    }

    [Fact]
    public void ParseChangedFiles_CapturesRenamePreviousPath()
    {
        string json = """[ { "filename": "Services/Renamed.cs", "status": "renamed", "previous_filename": "Services/Original.cs", "additions": 0, "deletions": 0 } ]""";

        var files = PrUpdateService.ParseChangedFiles(json);

        Assert.Single(files);
        Assert.Equal("renamed", files[0].Status);
        Assert.Equal("Services/Original.cs", files[0].PreviousFilename);
    }
}
