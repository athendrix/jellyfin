using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Auth.Oidc;

/// <summary>
/// Remembers which identity provider account (issuer and subject) each Jellyfin user is linked to.
/// </summary>
/// <remarks>
/// Users are matched to Jellyfin accounts by user name on their first login. The user name claim is not
/// guaranteed to be unique or stable, so the account is then pinned to the identity provider's subject, which is.
/// A different identity provider account that later presents the same user name is refused.
/// </remarks>
public sealed class OidcLinkStore
{
    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, string> _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLinkStore"/> class.
    /// </summary>
    /// <param name="path">The file holding the links, or null to keep them in memory only.</param>
    /// <param name="logger">The logger.</param>
    public OidcLinkStore(string? path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        _links = Load();
    }

    /// <summary>
    /// Builds the key identifying an identity provider account.
    /// </summary>
    /// <param name="issuer">The issuer.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>The key.</returns>
    public static string Key(string issuer, string subject) => issuer.TrimEnd('/') + "|" + subject;

    /// <summary>
    /// Gets the user linked to an identity provider account.
    /// </summary>
    /// <param name="key">The identity provider account key.</param>
    /// <returns>The user id, or null.</returns>
    public Guid? FindUser(string key)
    {
        lock (_lock)
        {
            foreach (var (userId, linked) in _links)
            {
                if (string.Equals(linked, key, StringComparison.Ordinal))
                {
                    return userId;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Gets the identity provider account a user is linked to.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The key, or null.</returns>
    public string? FindKey(Guid userId)
    {
        lock (_lock)
        {
            return _links.TryGetValue(userId, out var key) ? key : null;
        }
    }

    /// <summary>
    /// Links a user to an identity provider account, unlinking any other user from that account.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="key">The identity provider account key.</param>
    public void Link(Guid userId, string key)
    {
        lock (_lock)
        {
            // An identity provider account is linked to one user at a time.
            foreach (var other in _links.Where(kv => !kv.Key.Equals(userId) && string.Equals(kv.Value, key, StringComparison.Ordinal)).Select(kv => kv.Key).ToList())
            {
                _links.Remove(other);
            }

            _links[userId] = key;
            Save();
        }
    }

    /// <summary>
    /// Replaces a file with new contents so that it is never left half written, even on a crash.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The new contents.</param>
    internal static void WriteAtomically(string path, string contents)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(contents));
            stream.Flush(true);
        }

        File.Move(temp, path, true);
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
            // Failing closed: without the links, every account would be open to linking again.
            throw new InvalidOperationException($"Could not read the OpenID Connect account links from {_path}.", ex);
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            WriteAtomically(_path, JsonSerializer.Serialize(_links.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save the OpenID Connect account links to {Path}.", _path);
            throw;
        }
    }
}
