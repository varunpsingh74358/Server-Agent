using System.Text.RegularExpressions;

namespace CloudOrc.ControlAgent.Tests;

/// <summary>
/// A permanent, automated version of the manual repository-wide grep audits performed
/// during development - so a future change that accidentally reintroduces a
/// hardcoded/environment-specific value fails CI immediately instead of relying on
/// someone remembering to grep again. Scans `src/` and `installer/` only (production
/// code) - `tools/CloudOrc.AgentTestServer` is the explicitly-allowed dev/test-only
/// exception (it legitimately binds to `localhost`) and is deliberately excluded.
/// </summary>
public class StaticSourceAuditTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string[] ScannedRoots = { "src", "installer" };

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "CloudOrc.WindowsAgents.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException("Could not locate the repository root (CloudOrc.WindowsAgents.sln) from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string Path, string Content)> ScannedFiles()
    {
        foreach (var root in ScannedRoots)
        {
            var rootPath = Path.Combine(RepoRoot, root);
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.EndsWith(".dll") || file.EndsWith(".exe") || file.EndsWith(".pdb"))
                {
                    continue;
                }

                yield return (file, File.ReadAllText(file));
            }
        }
    }

    [Fact]
    public void SourceCode_ContainsNoHardcodedTestOrDeveloperPort18081()
    {
        var offenders = ScannedFiles().Where(f => f.Content.Contains("18081")).Select(f => f.Path).ToList();
        Assert.True(offenders.Count == 0, "Found hardcoded port 18081 in: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("10.247.239.113")]
    [InlineData("163.223.145.175")]
    [InlineData("163.223.145.25")]
    [InlineData("172.27.176.1")]
    [InlineData("172.29.208.1")]
    public void SourceCode_ContainsNoKnownDeveloperOrTestIPs(string ip)
    {
        var offenders = ScannedFiles().Where(f => f.Content.Contains(ip)).Select(f => f.Path).ToList();
        Assert.True(offenders.Count == 0, $"Found hardcoded IP '{ip}' in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void SourceCode_ContainsNoGenericPrivateIpLiterals()
    {
        // Broader safety net beyond the specific IPs above: any literal private-range IP
        // address anywhere in production source/installer code is suspicious - the only
        // legitimate values here are symbolic (config keys, {app}, {tmp}, etc.).
        var pattern = new Regex(@"\b(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3})\b");
        var offenders = ScannedFiles()
            .Where(f => pattern.IsMatch(f.Content))
            .Select(f => (f.Path, Match: pattern.Match(f.Content).Value))
            .ToList();
        Assert.True(offenders.Count == 0, "Found private IP literal(s): " + string.Join(", ", offenders.Select(o => $"{o.Path} -> {o.Match}")));
    }

    [Fact]
    public void SourceCode_HasNoWinRMDependency()
    {
        var offenders = ScannedFiles()
            .Where(f => f.Content.Contains("WinRM", StringComparison.OrdinalIgnoreCase) || f.Content.Contains("WSMan", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path)
            .ToList();
        Assert.True(offenders.Count == 0, "Found a WinRM/WSMan reference in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void SourceCode_HasNoRdpDependency()
    {
        var offenders = ScannedFiles()
            .Where(f => Regex.IsMatch(f.Content, @"\bRDP\b", RegexOptions.IgnoreCase) || f.Content.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path)
            .ToList();
        Assert.True(offenders.Count == 0, "Found an RDP/Remote Desktop reference in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void SourceCode_NeverHardcodesAPlaintextPasswordOrCredentialLiteral()
    {
        // Matches an assignment-style literal like password="...", pwd:'...', secret = "..."
        // with a non-empty value - the codebase's real design uses DPAPI-encrypted
        // storage and runtime-supplied tokens/credentials, never a literal in source.
        var pattern = new Regex(@"(?i)\b(password|pwd|secret|apikey|api_key)\s*[:=]\s*[""'][^""'\s]{3,}[""']");
        var offenders = ScannedFiles()
            .Where(f => pattern.IsMatch(f.Content))
            .Select(f => f.Path)
            .ToList();
        Assert.True(offenders.Count == 0, "Found a possible hardcoded credential literal in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void SourceCode_ContainsNoHardcodedBackendUrl()
    {
        // The only acceptable literal ws/wss/http/https URLs in production code are
        // inside XML doc comments illustrating the config format (which do not contain
        // "://" immediately followed by a non-placeholder host used as a real default) -
        // BackendConnectionOptions.Url must default to empty, never a real address.
        var offenders = ScannedFiles()
            .Where(f => Regex.IsMatch(f.Content, @"(?<!///[^\n]*)\b(ws|wss)://(?!localhost|your-|<)[a-zA-Z0-9.-]+"))
            .Select(f => f.Path)
            .ToList();
        Assert.True(offenders.Count == 0, "Found a literal non-placeholder ws(s):// URL in: " + string.Join(", ", offenders));
    }
}
