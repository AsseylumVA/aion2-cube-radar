using CubeRadar.Core.Protocol;

namespace CubeRadar.Core.Radar;

public sealed partial class RadarEngine
{
    private readonly Dictionary<uint, TrackedPlayer> _players = new();
    private PlayerFaction _configuredFaction;
    private PlayerFaction _detectedFaction;
    private int _knownEnemies;
    private PlayerFaction EffectiveOwnFaction => _configuredFaction != PlayerFaction.Unknown ? _configuredFaction : _detectedFaction;

    private sealed class TrackedPlayer(PlayerIdentity identity, long t)
    {
        public PlayerIdentity Identity { get; set; } = identity;
        public long FirstSeenMs { get; } = t;
        public long LastSeenMs { get; set; } = t;
        public PositionFix? Position { get; set; }
        public long PositionMs { get; set; }
    }

    /// <summary>Auto uses a validated self identity; a manual choice also works when capture starts mid-session.</summary>
    public void SetOwnFaction(string faction)
    {
        var parsed = faction.Trim().ToLowerInvariant() switch
        {
            "auto" => PlayerFaction.Unknown,
            "elyos" => PlayerFaction.Elyos,
            "asmodian" => PlayerFaction.Asmodian,
            _ => throw new FormatException("Eigene Fraktion: Auto, Elyos oder Asmodian wählen."),
        };
        lock (_gate)
        {
            _configuredFaction = parsed;
            ObserveEnemies(LastPacketMs);
        }
    }

    private void OnSelfIdentity(ReadOnlySpan<byte> body)
    {
        if (PlayerMessages.TryParseIdentity(body, isSelf: true, out var identity))
        {
            _detectedFaction = identity.Faction;
            if (_configuredFaction == PlayerFaction.Unknown) ObserveEnemies(LastPacketMs);
        }
    }

    private void ClearPlayerContacts()
    {
        _players.Clear();
        _knownEnemies = 0;
    }

    private void OnPlayerRecord(RecordKind kind, long t, ReadOnlySpan<byte> body)
    {
        switch (kind)
        {
            case RecordKind.PlayerInfo:
                if (!PlayerMessages.TryParseIdentity(body, isSelf: false, out var identity) || identity.EntityId == SelfId) return;
                if (_players.TryGetValue(identity.EntityId, out var known))
                {
                    if (t < known.LastSeenMs) return;
                    known.Identity = identity;
                    known.LastSeenMs = t;
                }
                else _players[identity.EntityId] = new TrackedPlayer(identity, t);
                break;
            case RecordKind.PlayerPosition:
            case RecordKind.PlayerMove:
            case RecordKind.PlayerMoveAlt:
            case RecordKind.PlayerMoveVelocity:
                if (PlayerMessages.TryParsePosition(body, absolute: kind == RecordKind.PlayerPosition, out var fix))
                    UpdatePlayerPosition(fix, t);
                break;
            case RecordKind.PlayerRemoved:
                if (PlayerMessages.TryParseRemoved(body, out var removed)) _players.Remove(removed);
                break;
            case RecordKind.PlayerKilled:
                // 04 8d starts with the victim, then u32, killer varint, server and name. Never create a contact here.
                var o = 0;
                if (Wire.TryVarint(body, ref o, out var victim) && body.Length >= o + 5) _players.Remove(victim);
                break;
        }
        if (kind is RecordKind.PlayerInfo or RecordKind.PlayerRemoved or RecordKind.PlayerKilled) ObserveEnemies(t);
    }

    private void UpdatePlayerPosition(PositionFix fix, long t)
    {
        if (fix.EntityId == SelfId || !_players.TryGetValue(fix.EntityId, out var player) || t < player.LastSeenMs) return;
        player.Position = fix;
        player.PositionMs = t;
        player.LastSeenMs = t;
    }

    private List<PlayerView> PlayerViews(long now, double? ox, double? oy, double? oz)
    {
        // Bounded memory after packet loss. A stale last-known position is explicitly flagged, not shown as live.
        foreach (var id in _players.Where(kv => now - kv.Value.LastSeenMs > 120_000).Select(kv => kv.Key).ToList())
            _players.Remove(id);
        ObserveEnemies(now);
        var ownEstimated = SelfFix is null || now - SelfFixMs > _cfg.FixFreshSeconds * 1000 ||
                           (Ring is { } ring && ring.TimeMs > SelfFixMs);
        return _players.Values.Where(p => p.Identity.EntityId != SelfId).Select(p =>
        {
            var i = p.Identity;
            bool? enemy = ClassifyEnemy(i);
            var pos = p.Position;
            double? distance = pos is { } f && ox is { } x && oy is { } y
                ? Units.Planar(x, y, f.X, f.Y) / Units.PerMetre : null;
            double? bearing = pos is { } fp && ox is { } px && oy is { } py ? _north.Bearing(px, py, fp.X, fp.Y) : null;
            long? age = pos is not null ? Math.Max(0, now - p.PositionMs) : null;
            return new PlayerView(i.EntityId, i.Name, i.Faction.ToString(), i.RankCode,
                PvpRanks.Name(i.RankCode), PvpRanks.IsVerified(i.RankCode), enemy,
                pos?.X, pos?.Y, pos?.Z, p.FirstSeenMs, p.LastSeenMs, pos is not null ? p.PositionMs : null, age,
                age is null or > 10_000, distance, distance is not null && (ownEstimated || age > 10_000),
                bearing, bearing is { } b ? Orientation.Compass(b) : null,
                pos is { } z ? z.Z - oz : null);
        }).OrderBy(p => p.IsEnemy == true ? 0 : p.IsEnemy is null ? 1 : 2)
          .ThenBy(p => p.DistanceM ?? double.MaxValue).ToList();
    }

    /// <summary>One alert when the view goes from no identified enemy to at least one. A further enemy while one is
    /// already shown does not alert again. Contacts quiet for 120 s no longer count.</summary>
    private void ObserveEnemies(long now)
    {
        var n = 0;
        foreach (var p in _players.Values)
        {
            if (p.Identity.EntityId == SelfId || now - p.LastSeenMs > 120_000) continue;
            if (IsEnemy(p.Identity)) n++;
        }
        var previous = _knownEnemies;
        _knownEnemies = n;
        if (previous == 0 && n > 0) EnemyAppeared?.Invoke();
    }

    private bool IsEnemy(PlayerIdentity i) => ClassifyEnemy(i) == true;

    /// <summary>
    /// The byte after gender is the client's mark: 1 hostile, 2 own server, anything else not hostile.
    /// Race does not override it. Dungeon Elyos were 0, 8, 64, 128, 136 or 192 at that byte and were not hostile in game.
    /// </summary>
    private bool? ClassifyEnemy(PlayerIdentity i) => i.Hostile == true ? true : i.Hostile == false ? false : null;
}

public sealed record PlayerView(uint EntityId, string Name, string Faction, uint? RankCode, string? RankName, bool RankVerified, bool? IsEnemy,
    double? X, double? Y, double? Z, long FirstSeenMs, long LastSeenMs, long? PositionMs, long? PositionAgeMs,
    bool PositionStale, double? DistanceM, bool DistanceEstimated, double? Bearing, string? Compass, double? DzUnits)
{
    public string RankSymbol => PvpRanks.Symbol(RankCode);
}
