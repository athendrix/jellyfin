using System;
using System.Threading;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.SyncPlay;

namespace MediaBrowser.Controller.SyncPlay.PlaybackRequests
{
    /// <summary>
    /// Class LoadProgressGroupRequest, a member reporting how far along it is in loading the media.
    /// </summary>
    /// <remarks>
    /// The report is informational: it does not change the state of the group. It keeps the group waiting
    /// for a member that is slow but alive, and lets the other members see what they are waiting for.
    /// </remarks>
    public class LoadProgressGroupRequest : AbstractPlaybackRequest
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LoadProgressGroupRequest"/> class.
        /// </summary>
        /// <param name="playlistItemId">The playlist item the member is loading.</param>
        /// <param name="progress">The loading progress, between 0 and 1, or null when the member cannot tell.</param>
        /// <param name="stage">What the member is doing.</param>
        public LoadProgressGroupRequest(Guid playlistItemId, double? progress, GroupMemberLoadStage stage)
        {
            PlaylistItemId = playlistItemId;
            Progress = progress;
            Stage = stage;
        }

        /// <summary>
        /// Gets the playlist item the member is loading.
        /// </summary>
        public Guid PlaylistItemId { get; }

        /// <summary>
        /// Gets the loading progress, between 0 and 1, or null when the member cannot tell.
        /// </summary>
        public double? Progress { get; }

        /// <summary>
        /// Gets what the member is doing.
        /// </summary>
        public GroupMemberLoadStage Stage { get; }

        /// <inheritdoc />
        public override PlaybackRequestType Action { get; } = PlaybackRequestType.LoadProgress;

        /// <inheritdoc />
        public override void Apply(IGroupStateContext context, IGroupState state, SessionInfo session, CancellationToken cancellationToken)
        {
            // Progress does not depend on, nor change, the state of the group.
            context.UpdateLoadProgress(session, PlaylistItemId, Progress, Stage);
        }
    }
}
