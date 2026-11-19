using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Server.Helpers;

/// <summary>
/// Decides how browsers and the desktop app may cache the files of the hosted web client.
/// </summary>
internal static partial class WebClientCachePolicy
{
    /// <summary>
    /// Cache-Control for files whose name contains a hash of their content. Their content never changes.
    /// </summary>
    internal const string Immutable = "public, max-age=31536000, immutable";

    /// <summary>
    /// Cache-Control for every other file. Clients may keep a copy, but must check it is current before using it.
    /// </summary>
    internal const string Revalidate = "no-cache";

    /// <summary>
    /// Gets the Cache-Control header value for a file of the web client.
    /// </summary>
    /// <param name="fileName">The name of the file.</param>
    /// <returns>The Cache-Control header value.</returns>
    /// <remarks>
    /// Files such as index.html, main.bundle.js or config.json keep their name across web client versions.
    /// Without a Cache-Control header, clients guess how long to keep them from their age, and the desktop
    /// app's disk cache could keep using an old main.bundle.js after an update. That bundle refers to chunks
    /// the new version no longer has, which leaves the client broken until its cache is cleared.
    /// </remarks>
    internal static string GetCacheControl(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return ContentHashPattern().IsMatch(fileName) ? Immutable : Revalidate;
    }

    // Webpack writes content hashes as a run of at least 16 hexadecimal digits between dots, e.g. "123.0c1a2b3c4d5e6f7a8b9c.chunk.js".
    [GeneratedRegex(@"\.[0-9a-f]{16,}\.", RegexOptions.CultureInvariant)]
    private static partial Regex ContentHashPattern();
}
