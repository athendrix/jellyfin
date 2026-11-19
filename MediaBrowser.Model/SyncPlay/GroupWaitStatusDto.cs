using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.SyncPlay;

/// <summary>
/// Class GroupWaitStatusDto, the readiness of every member of a SyncPlay group.
/// </summary>
public class GroupWaitStatusDto
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GroupWaitStatusDto"/> class.
    /// </summary>
    /// <param name="state">The state of the group.</param>
    /// <param name="waitingSince">When the group started waiting, if it is waiting.</param>
    /// <param name="members">The status of each member.</param>
    public GroupWaitStatusDto(GroupStateType state, DateTime? waitingSince, IReadOnlyList<GroupMemberStatusDto> members)
    {
        State = state;
        WaitingSince = waitingSince;
        Members = members;
    }

    /// <summary>
    /// Gets the state of the group.
    /// </summary>
    public GroupStateType State { get; }

    /// <summary>
    /// Gets the UTC time at which the group started waiting for its members, or null when it is not waiting.
    /// </summary>
    public DateTime? WaitingSince { get; }

    /// <summary>
    /// Gets the status of each member, in the order they joined.
    /// </summary>
    public IReadOnlyList<GroupMemberStatusDto> Members { get; }
}
