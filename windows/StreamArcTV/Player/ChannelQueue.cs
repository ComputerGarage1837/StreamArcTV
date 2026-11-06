using StreamArcTV.Data;

namespace StreamArcTV.Player;

/// The channels shown in the guide when a live channel was opened full screen, so the player can
/// move to the next or previous one (channel up / down) without going back to the guide.
public static class ChannelQueue
{
    private static List<Data.Stream> _channels = new();
    private static int _index = -1;

    public static Service? Service { get; private set; }

    public static void Set(Service service, IEnumerable<Data.Stream> list, Data.Stream current)
    {
        Service = service;
        _channels = list.ToList();
        _index = _channels.FindIndex(s => s.StreamId != null && s.StreamId == current.StreamId);
    }

    public static void Clear() { _channels = new(); _index = -1; Service = null; }

    public static bool Available => Service != null && _channels.Count > 1 && _index >= 0 && _index < _channels.Count;

    /// Moves by `delta` (wrapping round) and returns the channel there.
    public static Data.Stream? Step(int delta)
    {
        if (!Available) return null;
        _index = ((_index + delta) % _channels.Count + _channels.Count) % _channels.Count;
        return _channels[_index];
    }
}
