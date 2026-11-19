using System;
using MediaBrowser.Model.SyncPlay;

namespace Jellyfin.Api.Models.SyncPlayDtos;

/// <summary>
/// Class LoadProgressRequestDto.
/// </summary>
public class LoadProgressRequestDto
{
    /// <summary>
    /// Gets or sets the playlist item identifier of the item being loaded.
    /// </summary>
    /// <value>The playlist item identifier.</value>
    public Guid PlaylistItemId { get; set; }

    /// <summary>
    /// Gets or sets the loading progress, between 0 and 1, or null when the client cannot tell.
    /// </summary>
    /// <value>The loading progress.</value>
    public double? Progress { get; set; }

    /// <summary>
    /// Gets or sets what the client is doing.
    /// </summary>
    /// <value>The load stage.</value>
    public GroupMemberLoadStage Stage { get; set; }
}
