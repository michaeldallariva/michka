using Windows.Media.Control;

namespace DochkaDock.Services;

/// <summary>Snapshot of the system's current media session (whatever app
/// Windows considers the "current" one - same source its own volume flyout
/// media overlay uses).</summary>
public sealed record MediaSessionInfo(string SourceAppUserModelId, string Title, string Artist, bool IsPlaying);

/// <summary>Wraps GlobalSystemMediaTransportControlsSessionManager (WinRT) -
/// the same system-wide media session API Windows' own UI uses, works for
/// any app that implements the System Media Transport Controls (Spotify,
/// VLC, browsers playing video, etc.) without needing to know about that
/// app specifically. Polled on a timer by MainWindow rather than wiring
/// WinRT's own change events, consistent with this codebase's existing
/// poll-based reactivity (RunningAppsService/_runningAppsTimer).</summary>
public sealed class MediaSessionService
{
    /// <summary>Null if nothing is currently playing/paused anywhere, or the
    /// WinRT call fails for any reason (never throws).</summary>
    public async Task<MediaSessionInfo?> GetCurrentSessionAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager?.GetCurrentSession();
            if (session is null) return null;

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            return new MediaSessionInfo(
                session.SourceAppUserModelId ?? string.Empty,
                props?.Title ?? string.Empty,
                props?.Artist ?? string.Empty,
                isPlaying);
        }
        catch
        {
            return null;
        }
    }

    public async Task PlayAsync() => await WithCurrentSession(s => s.TryPlayAsync().AsTask());
    public async Task PauseAsync() => await WithCurrentSession(s => s.TryPauseAsync().AsTask());
    public async Task NextAsync() => await WithCurrentSession(s => s.TrySkipNextAsync().AsTask());
    public async Task PreviousAsync() => await WithCurrentSession(s => s.TrySkipPreviousAsync().AsTask());

    private static async Task WithCurrentSession(Func<GlobalSystemMediaTransportControlsSession, Task> action)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager?.GetCurrentSession();
            if (session is not null) await action(session);
        }
        catch
        {
            // Best-effort: the session may have ended between the menu
            // opening and the user clicking a transport button.
        }
    }
}
