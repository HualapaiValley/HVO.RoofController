using System.Net.Http.Headers;
using System.Reflection;
using HVO.RoofControllerV4.Common;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The <c>User-Agent</c> every REST request carries: the program's name and version (<c>hvo-roof/4.2.0</c>), so the
/// controller's command history can say which client sent a command. It identifies, and proves, nothing.
/// </summary>
internal static class RoofUserAgent
{
    private static readonly Lazy<(string Name, string? Version)> Program = new(() => Of(Assembly.GetEntryAssembly()));

    /// <summary>For <paramref name="program"/>, or the process's entry assembly when it is null.</summary>
    public static ProductInfoHeaderValue Create(Assembly? program = null)
    {
        var (name, version) = program is null ? Program.Value : Of(program);
        return new ProductInfoHeaderValue(name, version);
    }

    /// <summary>
    /// <paramref name="assembly"/>'s name and version without the commit, or the client library's when it has none
    /// or either is not a valid header token.
    /// </summary>
    internal static (string Name, string? Version) Of(Assembly? assembly)
    {
        foreach (var candidate in new[] { assembly, typeof(RoofUserAgent).Assembly })
        {
            if (candidate?.GetName().Name is not { Length: > 0 } name)
            {
                continue;
            }

            var version = RoofProductVersion.WithoutCommit(RoofProductVersion.Of(candidate));
            try
            {
                _ = new ProductInfoHeaderValue(name, version);
                return (name, version);
            }
            catch (FormatException)
            {
            }
        }

        return ("HVO.RoofControllerV4.Client", null);
    }
}
