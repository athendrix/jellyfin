namespace MediaBrowser.Model.SyncPlay;

/// <summary>
/// Class GroupMemberStatusDto, the readiness of one member of a SyncPlay group.
/// </summary>
public class GroupMemberStatusDto
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GroupMemberStatusDto"/> class.
    /// </summary>
    /// <param name="userName">The user name of the member.</param>
    /// <param name="deviceName">The device name of the member.</param>
    /// <param name="isReady">Whether the member is ready.</param>
    /// <param name="isIgnoringWait">Whether the member has opted out of the group wait.</param>
    /// <param name="stage">What the member is doing.</param>
    /// <param name="progress">The loading progress of the member, between 0 and 1, if known.</param>
    /// <param name="isStalled">Whether the member stopped reporting while the group waits for it.</param>
    public GroupMemberStatusDto(string userName, string deviceName, bool isReady, bool isIgnoringWait, GroupMemberLoadStage stage, double? progress, bool isStalled)
    {
        UserName = userName;
        DeviceName = deviceName;
        IsReady = isReady;
        IsIgnoringWait = isIgnoringWait;
        Stage = stage;
        Progress = progress;
        IsStalled = isStalled;
    }

    /// <summary>
    /// Gets the user name of the member.
    /// </summary>
    public string UserName { get; }

    /// <summary>
    /// Gets the device name of the member.
    /// </summary>
    public string DeviceName { get; }

    /// <summary>
    /// Gets a value indicating whether the member is ready to play.
    /// </summary>
    public bool IsReady { get; }

    /// <summary>
    /// Gets a value indicating whether the member has opted out of the group wait.
    /// </summary>
    public bool IsIgnoringWait { get; }

    /// <summary>
    /// Gets what the member is doing.
    /// </summary>
    public GroupMemberLoadStage Stage { get; }

    /// <summary>
    /// Gets the loading progress of the member, between 0 and 1, or null when the member cannot tell.
    /// </summary>
    public double? Progress { get; }

    /// <summary>
    /// Gets a value indicating whether the member stopped reporting while the group waits for it.
    /// </summary>
    public bool IsStalled { get; }
}
