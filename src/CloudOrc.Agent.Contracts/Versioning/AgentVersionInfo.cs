using System.Reflection;

namespace CloudOrc.Agent.Contracts.Versioning;

/// <summary>
/// The one place every agent reads its own version from, so the version reported by
/// <c>--version</c>, sent during enrollment, and sent in the WebSocket HELLO message can
/// never drift apart. Reads <see cref="AssemblyInformationalVersionAttribute"/> (the
/// <c>&lt;Version&gt;</c> MSBuild property, set solution-wide by Directory.Build.props)
/// off the given assembly, falling back to <see cref="AssemblyFileVersionAttribute"/> and
/// then a hardcoded default only if neither attribute is present at all.
/// </summary>
public static class AgentVersionInfo
{
    private const string FallbackVersion = "0.0.0";

    public static string GetVersion(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(AgentVersionInfo).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion;
        }

        return FallbackVersion;
    }
}
