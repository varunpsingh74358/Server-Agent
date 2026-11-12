using CloudOrc.ControlAgent.Commands;

namespace CloudOrc.ControlAgent.Tests.Commands;

/// <summary>Folder picker (discovery.browse): read-only, local-only, never escapes the allowed roots.</summary>
public sealed class DiscoveryBrowseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudorc-browse-" + Guid.NewGuid().ToString("N"));

    public DiscoveryBrowseTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "customer", "CloudDrive", "Tally"));
        Directory.CreateDirectory(Path.Combine(_root, "other-customer"));
        File.WriteAllText(Path.Combine(_root, "customer", "CloudDrive", "ledger.csv"), "1,2,3");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void ListsFoldersFirst_WithFileSizes()
    {
        var (listing, error) = DiscoveryBrowseHandler.Browse(Path.Combine(_root, "customer", "CloudDrive"), [Path.Combine(_root, "customer")], 100);

        Assert.Null(error);
        Assert.Equal(["Tally", "ledger.csv"], listing!.Entries.Select(e => e.Name));
        Assert.Equal("dir", listing.Entries[0].Type);
        Assert.Equal(5, listing.Entries[1].Size);
    }

    [Fact]
    public void EmptyPath_ReturnsTheAllowedRoots()
    {
        var (listing, error) = DiscoveryBrowseHandler.Browse(null, [Path.Combine(_root, "customer")], 100);

        Assert.Null(error);
        Assert.Single(listing!.Roots);
        Assert.Empty(listing.Entries);
    }

    [Theory]
    [InlineData("other-customer")]
    [InlineData("customer\\..\\other-customer")]
    [InlineData("")]
    public void PathsOutsideTheAllowedRoots_AreRefused(string relative)
    {
        var (_, error) = DiscoveryBrowseHandler.Browse(Path.Combine(_root, relative), [Path.Combine(_root, "customer")], 100);
        Assert.Equal("PATH_NOT_ALLOWED", error!.Value.Code);
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData("relative\\path")]
    public void NonLocalPaths_AreRefused(string path)
    {
        var (_, error) = DiscoveryBrowseHandler.Browse(path, [], 100);
        Assert.Equal("PATH_NOT_ALLOWED", error!.Value.Code);
    }

    [Fact]
    public void JunctionInsideAllowedRoot_IsShownAsLink_AndCannotBeBrowsed()
    {
        var link = Path.Combine(_root, "customer", "escape");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_root, "other-customer"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // creating links needs privileges on some machines; the guard is still covered by PassesThroughLink
        }

        var (listing, _) = DiscoveryBrowseHandler.Browse(Path.Combine(_root, "customer"), [Path.Combine(_root, "customer")], 100);
        Assert.Equal("link", listing!.Entries.Single(e => e.Name == "escape").Type);

        var (_, error) = DiscoveryBrowseHandler.Browse(link, [Path.Combine(_root, "customer")], 100);
        Assert.Equal("PATH_NOT_ALLOWED", error!.Value.Code);
    }

    [Fact]
    public void EntryLimit_MarksTheListingTruncated()
    {
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_root, "customer", $"f{i}.txt"), "x");
        }

        var (listing, _) = DiscoveryBrowseHandler.Browse(Path.Combine(_root, "customer"), [Path.Combine(_root, "customer")], 3);
        Assert.True(listing!.Truncated);
        Assert.Equal(3, listing.Entries.Count);
    }
}
