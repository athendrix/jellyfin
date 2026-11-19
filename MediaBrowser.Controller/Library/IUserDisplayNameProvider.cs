using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Provides the names shown for users outside of user management, such as a name taken from an identity provider.
/// </summary>
public interface IUserDisplayNameProvider
{
    /// <summary>
    /// Gets the name to show for a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The display name, or null to show the user name.</returns>
    string? GetDisplayName(Guid userId);
}
