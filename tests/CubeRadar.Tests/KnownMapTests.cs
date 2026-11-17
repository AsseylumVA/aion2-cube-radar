using CubeRadar.Core.Radar;

namespace CubeRadar.Tests;

public class KnownMapTests
{
    private static byte[] MapLoad(int mapId)
    {
        var body = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)mapId);
        return body;
    }

    [Fact]
    public void Before_any_map_packet_the_map_is_unknown()
    {
        var engine = new RadarEngine(new RadarConfig());
        var snap = engine.Snapshot();
        Assert.Null(snap.MapId);
        Assert.False(snap.KnownMap);
    }

    [Fact]
    public void Open_world_map_is_known_and_a_dungeon_world_id_is_kept_but_not_known()
    {
        var engine = new RadarEngine(new RadarConfig());
        engine.OnFrame(1_000, 0x2136, MapLoad(1110));
        var open = engine.Snapshot();
        Assert.Equal(1110, open.MapId);
        Assert.Equal("Altgard", open.Zone);
        Assert.True(open.KnownMap);

        engine.OnFrame(2_000, 0x2136, MapLoad(600022));
        var dungeon = engine.Snapshot();
        Assert.Equal(600022, dungeon.MapId);
        Assert.False(dungeon.KnownMap);

        engine.OnFrame(3_000, 0x2136, MapLoad(1110));
        Assert.True(engine.Snapshot().KnownMap);
    }

    [Fact]
    public void Restored_open_world_map_is_known_before_the_next_load_packet()
    {
        var engine = new RadarEngine(new RadarConfig());
        engine.RestoreMap(1110);
        var snap = engine.Snapshot();
        Assert.Equal(1110, snap.MapId);
        Assert.True(snap.KnownMap);

        var dungeon = new RadarEngine(new RadarConfig());
        dungeon.RestoreMap(600022);
        Assert.Equal(600022, dungeon.MapId);
        Assert.False(dungeon.Snapshot().KnownMap);
    }
}
