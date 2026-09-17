using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Monitor.Api;

/// <summary>
/// Shared, in-memory auth state for the hub: a per-key failed-attempt rate limiter (used by the agent
/// token check on <c>/api/ingest</c> and the UI PIN login) plus the browser PIN-session store. After a
/// handful of wrong tokens/PINs from one source it locks that source out for a growing cool-down, so a
/// short token/PIN is safe against online guessing. Sessions live only in memory and the PIN cookie is
/// session-scoped, so a hub restart or closing the browser logs everyone out. Registered as a singleton.
/// </summary>
public sealed class AuthState
{
    private sealed class Bucket
    {
        public int Fails;
        public long WindowMs;     // start of the current counting window (TickCount64)
        public long LockUntilMs;  // locked out until this tick
        public int LockCount;     // how many times this key has been locked (drives escalation)
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    private const int MaxFails = 5;          // wrong attempts allowed in the window before a lockout
    private const long WindowMs = 120_000;   // counting window for failures
    private const long BaseLockMs = 120_000; // first lockout length; doubles, trebles, ... on repeat
    private const int MaxLockMult = 10;      // cap the escalation (10 * 120s = 20 min)

    private long _lastSweep;

    /// <summary>True when <paramref name="key"/> is currently locked out; <paramref name="retryAfterSec"/>
    /// gets the remaining cool-down in seconds.</summary>
    public bool IsLocked(string key, out int retryAfterSec)
    {
        retryAfterSec = 0;
        if (_buckets.TryGetValue(key, out var b))
        {
            long now = Environment.TickCount64;
            if (now < b.LockUntilMs)
            {
                retryAfterSec = (int)((b.LockUntilMs - now + 999) / 1000);
                return true;
            }
        }
        return false;
    }

    /// <summary>Record a failed attempt for <paramref name="key"/>; trip an escalating lockout once the
    /// failure threshold is reached within the window.</summary>
    public void RecordFailure(string key)
    {
        Sweep();
        long now = Environment.TickCount64;
        _buckets.AddOrUpdate(key,
            _ => new Bucket { Fails = 1, WindowMs = now },
            (_, b) =>
            {
                if (now - b.WindowMs > WindowMs) { b.Fails = 0; b.WindowMs = now; }
                b.Fails++;
                if (b.Fails >= MaxFails)
                {
                    b.LockCount = Math.Min(b.LockCount + 1, MaxLockMult);
                    b.LockUntilMs = now + BaseLockMs * b.LockCount;
                    b.Fails = 0;
                    b.WindowMs = now;
                }
                return b;
            });
    }

    /// <summary>Clear a key's failure/lockout state after a successful auth, so good clients are never
    /// throttled.</summary>
    public void RecordSuccess(string key) => _buckets.TryRemove(key, out _);

    // Occasionally drop stale buckets so the dictionary can't grow without bound under a flood from
    // many spoofed source IPs.
    private void Sweep()
    {
        long now = Environment.TickCount64;
        if (now - _lastSweep < 60_000) return;
        _lastSweep = now;
        foreach (var kv in _buckets)
            if (now - kv.Value.WindowMs > WindowMs && now >= kv.Value.LockUntilMs)
                _buckets.TryRemove(kv.Key, out _);
    }

    // ---- PIN browser sessions ------------------------------------------------------------------
    // sid -> expiry tick. The cookie carrying the sid is a *session* cookie (no Max-Age), so it dies
    // when the browser closes; this server-side expiry is just a sliding backstop.
    private readonly ConcurrentDictionary<string, long> _sessions = new();
    private static readonly long SessionTtlMs = 12L * 60 * 60 * 1000; // 12h idle backstop

    /// <summary>Open a new PIN session, returning its opaque id (set as an httpOnly cookie).</summary>
    public string NewSession()
    {
        Span<byte> b = stackalloc byte[24];
        RandomNumberGenerator.Fill(b);
        var sid = Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _sessions[sid] = Environment.TickCount64 + SessionTtlMs;
        return sid;
    }

    /// <summary>True if the session id is known and unexpired; slides the expiry on use.</summary>
    public bool ValidSession(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return false;
        if (!_sessions.TryGetValue(sid, out var exp)) return false;
        long now = Environment.TickCount64;
        if (now >= exp) { _sessions.TryRemove(sid, out _); return false; }
        _sessions[sid] = now + SessionTtlMs;
        return true;
    }

    /// <summary>End a single session (logout / lock).</summary>
    public void EndSession(string? sid) { if (!string.IsNullOrEmpty(sid)) _sessions.TryRemove(sid!, out _); }

    /// <summary>Drop every session, e.g. when the PIN is changed or disabled, forcing a re-login.</summary>
    public void ClearSessions() => _sessions.Clear();
}
