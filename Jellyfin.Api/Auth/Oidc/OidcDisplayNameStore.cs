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
    public void SetDisplayName(Guid userId, string? displayName)
    {
        var name = Sanitize(displayName);
        lock (_lock)
        {
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

            Save();
        }
    }

    /// <summary>
    /// Cleans up a display name: no control characters, trimmed, at most <see cref="MaxLength"/> characters.
    /// </summary>
    /// <param name="displayName">The display name.</param>
    /// <returns>The cleaned up display name, or null when nothing is left.</returns>
    internal static string? Sanitize(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var builder = new StringBuilder(displayName.Length);
        foreach (var c in displayName.Normalize(NormalizationForm.FormC))
        {
            if (!char.IsControl(c) && c is not ('​' or '‎' or '‏' or '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩'))
            {
                builder.Append(c);
            }
        }

        var name = builder.ToString().Trim();
        if (name.Length > MaxLength)
        {
            name = name[..MaxLength].TrimEnd();
        }

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

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_names.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value)));
            File.Move(temp, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save the OpenID Connect display names to {Path}.", _path);
        }
    }
}
