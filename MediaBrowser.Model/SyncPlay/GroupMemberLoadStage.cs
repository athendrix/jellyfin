namespace MediaBrowser.Model.SyncPlay;

/// <summary>
/// Enum GroupMemberLoadStage, what a SyncPlay group member is doing while the group waits for it.
/// </summary>
public enum GroupMemberLoadStage
{
    /// <summary>
    /// The member has not reported what it is doing.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The member is opening the media (fetching playback info, starting a transcode, loading the stream).
    /// </summary>
    Loading = 1,

    /// <summary>
    /// The member has the media open and is filling its buffer.
    /// </summary>
    Buffering = 2,

    /// <summary>
    /// The member is seeking to the group position.
    /// </summary>
    Seeking = 3,

    /// <summary>
    /// The member is ready to play.
    /// </summary>
    Ready = 4
}
