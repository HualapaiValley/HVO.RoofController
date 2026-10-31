using System.Net;

namespace HVO.RoofControllerV4.Alpaca.Devices;

/// <summary>
/// Which clients have connected to a device: a flag per client, kept here and nowhere else. Connecting and
/// disconnecting never touch the controller or the roof, and Disconnect never stops or moves it. A client is its
/// address and its <c>ClientID</c> (untrusted: it only tells apart the clients at one address). The flags are few and
/// expire: a client idle for <see cref="AlpacaOptions.ConnectionIdleMinutes"/> is forgotten, and connecting one past
/// <see cref="AlpacaOptions.MaxConnectedClients"/> forgets the one idle longest. A restart forgets them all.
/// </summary>
public sealed class AlpacaConnections(AlpacaOptions options, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<AlpacaClientKey, DateTimeOffset> _lastSeen = [];
    private readonly TimeSpan _idle = TimeSpan.FromMinutes(options.ConnectionIdleMinutes);
    private readonly int _capacity = options.MaxConnectedClients;

    /// <summary>How many clients are connected now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Expire(time.GetUtcNow());
                return _lastSeen.Count;
            }
        }
    }

    /// <summary>True when <paramref name="client"/> is connected; a connected client's request keeps it connected.</summary>
    public bool IsConnected(AlpacaClientKey client)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_lastSeen.TryGetValue(client, out var seen) && now - seen < _idle)
            {
                _lastSeen[client] = now;
                return true;
            }

            _lastSeen.Remove(client);
            return false;
        }
    }

    /// <summary>Connects <paramref name="client"/>.</summary>
    public void Connect(AlpacaClientKey client)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            Expire(now);
            if (!_lastSeen.ContainsKey(client) && _lastSeen.Count >= _capacity)
            {
                _lastSeen.Remove(_lastSeen.MinBy(entry => entry.Value).Key);
            }

            _lastSeen[client] = now;
        }
    }

    /// <summary>Disconnects <paramref name="client"/>. Nothing else happens: the roof is not stopped or moved.</summary>
    public void Disconnect(AlpacaClientKey client)
    {
        lock (_gate)
        {
            _lastSeen.Remove(client);
        }
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var (client, seen) in _lastSeen.Where(entry => now - entry.Value >= _idle).ToList())
        {
            _lastSeen.Remove(client);
        }
    }
}

/// <summary>One Alpaca client: its address and its ClientID.</summary>
public readonly record struct AlpacaClientKey(IPAddress Address, uint ClientId);
