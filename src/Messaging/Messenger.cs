namespace GIoC;

/// <summary>
/// A static, typed publish/subscribe message bus with optional channel routing.
/// Recipients are held via WeakReference — collected recipients are silently removed
/// on the next Send, preventing memory leaks.
///
/// Broadcast (no channel) — all subscribers receive it:
///   Messenger.Register&lt;DamageEvent&gt;(this, e => Health -= e.Amount);
///   Messenger.Send(new DamageEvent(10));
///
/// Channel — only subscribers on that channel receive it:
///   Messenger.Register&lt;DamageEvent&gt;(this, "player", e => Health -= e.Amount);
///   Messenger.Send(new DamageEvent(10), "player");
///
/// Broadcast subscribers also receive channeled sends.
/// Channel subscribers only receive sends on their channel.
///
/// Unsubscribe:
///   Messenger.Unregister(this);                          // all types, all channels
///   Messenger.Unregister&lt;DamageEvent&gt;(this);            // one type, all channels
///   Messenger.Unregister&lt;DamageEvent&gt;(this, "player");  // one type, one channel
/// </summary>
public static class Messenger
{
    private static readonly object _lock = new();
    // Key: (message type, channel) — null channel = broadcast
    private static readonly Dictionary<(Type, string?), List<Entry>> _subs = new();

    private sealed class Entry(WeakReference recipient, Delegate handler)
    {
        public readonly WeakReference Recipient = recipient;
        public readonly Delegate      Handler   = handler;
    }

    // ── Subscribe ─────────────────────────────────────────────────────────────

    /// <summary>Subscribe to TMessage on all channels (broadcast).</summary>
    public static void Register<TMessage>(object recipient, Action<TMessage> handler)
        => RegisterCore(recipient, handler, channel: null);

    /// <summary>Subscribe to TMessage on a specific channel only.</summary>
    public static void Register<TMessage>(object recipient, string channel, Action<TMessage> handler)
        => RegisterCore(recipient, handler, channel);

    private static void RegisterCore<TMessage>(object recipient, Action<TMessage> handler, string? channel)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            var key = (typeof(TMessage), channel);
            if (!_subs.TryGetValue(key, out var list))
                _subs[key] = list = [];
            list.Add(new Entry(new WeakReference(recipient), handler));
        }
    }

    // ── Unsubscribe ───────────────────────────────────────────────────────────

    /// <summary>Unregister all subscriptions (all types, all channels) for this recipient.</summary>
    public static void Unregister(object recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        lock (_lock)
        {
            foreach (var list in _subs.Values)
                list.RemoveAll(e => !e.Recipient.IsAlive || ReferenceEquals(e.Recipient.Target, recipient));
        }
    }

    /// <summary>Unregister all TMessage subscriptions (all channels) for this recipient.</summary>
    public static void Unregister<TMessage>(object recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var msgType = typeof(TMessage);
        lock (_lock)
        {
            foreach (var key in _subs.Keys.Where(k => k.Item1 == msgType).ToList())
                _subs[key].RemoveAll(e => !e.Recipient.IsAlive || ReferenceEquals(e.Recipient.Target, recipient));
        }
    }

    /// <summary>Unregister TMessage on a specific channel for this recipient.</summary>
    public static void Unregister<TMessage>(object recipient, string channel)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        lock (_lock)
        {
            var key = (typeof(TMessage), (string?)channel);
            if (_subs.TryGetValue(key, out var list))
                list.RemoveAll(e => !e.Recipient.IsAlive || ReferenceEquals(e.Recipient.Target, recipient));
        }
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Send a message. Delivers to:
    ///   - broadcast subscribers (no channel) — always
    ///   - channel subscribers matching <paramref name="channel"/> — only when channel is specified
    /// </summary>
    public static void Send<TMessage>(TMessage message, string? channel = null)
    {
        List<Entry> toInvoke = [];
        lock (_lock)
        {
            // Broadcast subscribers always receive every send
            Collect<TMessage>(null, toInvoke);
            // Channel subscribers only receive sends on their channel
            if (channel != null)
                Collect<TMessage>(channel, toInvoke);
        }
        foreach (var entry in toInvoke)
            ((Action<TMessage>)entry.Handler)(message);
    }

    /// <summary>Send a default-constructed message (useful for parameterless events).</summary>
    public static void Send<TMessage>(string? channel = null) where TMessage : new()
        => Send(new TMessage(), channel);

    private static void Collect<TMessage>(string? channel, List<Entry> result)
    {
        var key = (typeof(TMessage), channel);
        if (!_subs.TryGetValue(key, out var list)) return;
        list.RemoveAll(e => !e.Recipient.IsAlive);
        result.AddRange(list);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>Remove all subscriptions. Useful for test cleanup or full scene reset.</summary>
    public static void Reset()
    {
        lock (_lock) _subs.Clear();
    }
}
