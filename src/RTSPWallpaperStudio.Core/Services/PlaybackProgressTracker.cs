namespace RTSPWallpaperStudio.Core.Services;

/// <summary>
/// Tracks decoded media-time progress independently from LibVLC's Playing/Vout state.
/// A decoder can report Playing with a video output while it is still buffering or
/// catching up, so an implausibly large time jump is deliberately not counted as
/// healthy playback progress.
/// </summary>
public sealed class PlaybackProgressTracker
{
    private const double MaximumPlaybackRate = 2.0;
    private const int MinimumStableSamples = 4;
    private static readonly TimeSpan MinimumStableDuration = TimeSpan.FromMilliseconds(750);
    private readonly object _gate = new();
    private long _lastObservedMediaTimeMs = -1;
    private DateTimeOffset? _lastObservedAt;
    private DateTimeOffset? _lastProgressAt;
    private DateTimeOffset? _stableSince;
    private int _stableSamples;
    private double? _lastRate;

    public void Reset()
    {
        lock (_gate)
        {
            _lastObservedMediaTimeMs = -1;
            _lastObservedAt = null;
            _lastProgressAt = null;
            _stableSince = null;
            _stableSamples = 0;
            _lastRate = null;
        }
    }

    public bool Observe(long mediaTimeMs, DateTimeOffset observedAt)
    {
        if (mediaTimeMs < 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_lastObservedAt is null || _lastObservedMediaTimeMs < 0)
            {
                _lastObservedMediaTimeMs = mediaTimeMs;
                _lastObservedAt = observedAt;
                return false;
            }

            var elapsed = observedAt - _lastObservedAt.Value;
            var mediaDelta = mediaTimeMs - _lastObservedMediaTimeMs;
            _lastObservedMediaTimeMs = mediaTimeMs;
            _lastObservedAt = observedAt;

            if (elapsed <= TimeSpan.Zero)
            {
                _lastRate = mediaDelta > 0 ? double.PositiveInfinity : 0;
                return false;
            }

            if (mediaDelta == 0)
            {
                // The TimeChanged callback and the watchdog both observe the
                // same media time. A duplicate sample is not a rewind and must
                // not erase already accumulated stable-progress evidence.
                _lastRate = 0;
                return false;
            }

            if (mediaDelta < 0)
            {
                _stableSince = null;
                _stableSamples = 0;
                _lastRate = double.NegativeInfinity;
                return false;
            }

            var rate = mediaDelta / elapsed.TotalMilliseconds;
            _lastRate = rate;
            if (rate > MaximumPlaybackRate)
            {
                // This is the initial catch-up/fast-forward pattern. Keep the
                // observed time, but do not refresh the healthy-progress clock.
                _stableSince = null;
                _stableSamples = 0;
                return false;
            }

            _stableSince ??= observedAt;
            _stableSamples++;
            _lastProgressAt = observedAt;
            return true;
        }
    }

    public PlaybackProgressSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            var age = _lastProgressAt is null ? TimeSpan.MaxValue : now - _lastProgressAt.Value;
            var primed = _stableSince is not null
                         && _stableSamples >= MinimumStableSamples
                         && now - _stableSince.Value >= MinimumStableDuration;
            return new PlaybackProgressSnapshot(_lastObservedMediaTimeMs, _lastProgressAt, age,
                primed, _stableSamples, _lastRate);
        }
    }
}

public sealed record PlaybackProgressSnapshot(
    long MediaTimeMs,
    DateTimeOffset? LastProgressAt,
    TimeSpan ProgressAge,
    bool IsPrimed,
    int StableSamples,
    double? Rate);
