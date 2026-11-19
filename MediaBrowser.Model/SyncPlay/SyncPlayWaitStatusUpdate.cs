using System;
using System.ComponentModel;

namespace MediaBrowser.Model.SyncPlay;

/// <inheritdoc/>
public class SyncPlayWaitStatusUpdate : GroupUpdate<GroupWaitStatusDto>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SyncPlayWaitStatusUpdate"/> class.
    /// </summary>
    /// <param name="groupId">The groupId.</param>
    /// <param name="data">The data.</param>
    public SyncPlayWaitStatusUpdate(Guid groupId, GroupWaitStatusDto data) : base(groupId, data)
    {
    }

    /// <summary>
    /// Gets the update type.
    /// </summary>
    /// <value>The update type.</value>
    [DefaultValue(GroupUpdateType.WaitStatus)]
    public override GroupUpdateType Type => GroupUpdateType.WaitStatus;
}
