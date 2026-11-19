#nullable disable

using System;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.SyncPlay;

namespace MediaBrowser.Controller.SyncPlay
{
    /// <summary>
    /// Class GroupMember.
    /// </summary>
    public class GroupMember
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GroupMember"/> class.
        /// </summary>
        /// <param name="session">The session.</param>
        public GroupMember(SessionInfo session)
            : this(session, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GroupMember"/> class.
        /// </summary>
        /// <param name="session">The session.</param>
        /// <param name="displayName">The name shown for the user to the group, or null to show the user name.</param>
        public GroupMember(SessionInfo session, string displayName)
        {
            SessionId = session.Id;
            UserId = session.UserId;
            UserName = string.IsNullOrWhiteSpace(displayName) ? session.UserName : displayName;
            DeviceName = session.DeviceName;
        }

        /// <summary>
        /// Gets the identifier of the session.
        /// </summary>
        /// <value>The session identifier.</value>
        public string SessionId { get; }

        /// <summary>
        /// Gets the identifier of the user.
        /// </summary>
        /// <value>The user identifier.</value>
        public Guid UserId { get; }

        /// <summary>
        /// Gets the name shown for the user to the group: the display name when the user has one, the user name otherwise.
        /// </summary>
        /// <value>The username.</value>
        public string UserName { get; }

        /// <summary>
        /// Gets or sets the ping, in milliseconds.
        /// </summary>
        /// <value>The ping.</value>
        public long Ping { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this member is buffering.
        /// </summary>
        /// <value><c>true</c> if member is buffering; <c>false</c> otherwise.</value>
        public bool IsBuffering { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this member is following group playback.
        /// </summary>
        /// <value><c>true</c> to ignore member on group wait; <c>false</c> if they're following group playback.</value>
        public bool IgnoreGroupWait { get; set; }

        /// <summary>
        /// Gets the device name of the session.
        /// </summary>
        /// <value>The device name.</value>
        public string DeviceName { get; }

        /// <summary>
        /// Gets or sets what the session is doing while the group waits for it.
        /// </summary>
        /// <value>The load stage.</value>
        public GroupMemberLoadStage LoadStage { get; set; } = GroupMemberLoadStage.Unknown;

        /// <summary>
        /// Gets or sets the loading progress of the session, between 0 and 1, or null when unknown.
        /// </summary>
        /// <value>The loading progress.</value>
        public double? LoadProgress { get; set; }

        /// <summary>
        /// Gets or sets the time, in <see cref="System.Environment.TickCount64"/> milliseconds, the session last showed signs of life to the group.
        /// </summary>
        /// <value>The time the session was last seen.</value>
        public long LastSeen { get; set; }
    }
}
