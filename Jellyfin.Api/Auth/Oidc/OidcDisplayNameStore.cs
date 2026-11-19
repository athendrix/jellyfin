using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Auth.Oidc;

/// <summary>
/// Remembers the display name the identity provider gave each user at their last login, and shows it for the user
/// outside of user management.
/// </summary>
public sealed class OidcDisplayNameStore : IUserDisplayNameProvider
{
    /// <summary>
    /// The longest display name kept.
    /// </summary>
    internal const int MaxLength = 64;

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, string> _names;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcDisplayNameStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public OidcDisplayNameStore(IApplicationPaths applicationPaths, ILogger<OidcDisplayNameStore> logger)
        : this(Path.Combine(applicationPaths.DataPath, "oidc-display-names.json"), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcDisplayNameStore"/> class.
    /// </summary>
    /// <param name="path">The file holding the names, or null to keep them in memory only.</param>
    /// <param name="logger">The logger.</param>
    internal OidcDisplayNameStore(string? path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _names = Load();
    }

    /// <inheritdoc />
    public string? GetDisplayName(Guid userId)
    {
        lock (_lock)
        {
            return _names.TryGetValue(userId, out var name) ? name : null;
        }
    }

    /// <summary>
    /// Sets the display name of a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="displayName">The display name, or null to show the user name.</param>
    /// <param name="reservedNames">Names nobody may use as a display name, such as the user names of the other users.</param>
    public void SetDisplayName(Guid userId, string? displayName, IEnumerable<string>? reservedNames = null)
    {
        var name = Sanitize(displayName);
        lock (_lock)
        {
            // A display name that is another user's display name or user name would let one user pass for another.
            if (name is not null
                && (_names.Any(kv => !kv.Key.Equals(userId) && string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
                    || (reservedNames?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false)))
            {
                _logger.LogWarning("The display name of user {UserId} is already used by another user; showing the user name instead.", userId);
                name = null;
            }

            var known = _names.TryGetValue(userId, out var current);
            if (name is null ? !known : string.Equals(current, name, StringComparison.Ordinal))
            {
                return;
            }

            if (name is null)
            {
                _names.Remove(userId);
            }
            else
            {
                _names[userId] = name;
            }

            if (!Save())
            {
                // Keep memory and file consistent.
                if (known)
                {
                    _names[userId] = current!;
                }
                else
                {
                    _names.Remove(userId);
                }
            }
        }
    }

    /// <summary>
    /// Cleans up a display name: no invisible or direction-changing characters, single spaces, at most
    /// <see cref="MaxLength"/> characters.
    /// </summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>The cleaned up display name, or null when nothing is left.</returns>
    internal static string? Sanitize(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = displayName.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not valid UTF-16 (a lone surrogate).
            return null;
        }

        // Whole characters (runes), so a character outside the basic plane is never cut in half. Invisible and
        // direction-changing characters (formats, controls, separators other than the space, private use) are dropped.
        var builder = new StringBuilder(normalized.Length);
        var count = 0;
        foreach (var current in normalized.EnumerateRunes())
        {
            var rune = current;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (category == System.Globalization.UnicodeCategory.SpaceSeparator)
            {
                // Any kind of space becomes a plain one.
                rune = new Rune(' ');
            }

            if (count == MaxLength)
            {
                break;
            }

            builder.Append(rune.ToString());
            count++;
        }

        var name = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? null : name;
    }

    private Dictionary<Guid, string> Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return new Dictionary<Guid, string>();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<Dictionary<Guid, string>>(stream) ?? new Dictionary<Guid, string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Display names are cosmetic: start over rather than fail.
            _logger.LogError(ex, "Could not read the OpenID Connect display names from {Path}.", _path);
            return new Dictionary<Guid, string>();
        }
    }

    private bool Save()
    {
        if (_path is null)
        {
            return true;
        }

        try
        {
            OidcLinkStore.WriteAtomically(_path, JsonSerializer.Serialize(_names.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogError(ex, "Could not save the OpenID Connect display names to {Path}.", _path);
            return false;
        }
    }
}
