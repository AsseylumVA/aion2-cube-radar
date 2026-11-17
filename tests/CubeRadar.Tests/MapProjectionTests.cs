using CubeRadar.Core.Radar;

namespace CubeRadar.Tests;

public class MapProjectionTests
{
    // Altgard / Verteron open-world fit from maps.json (v0.9.7+): 1024 px per 102000 cm.
    private const double Scale = 1024.0 / 102000.0;
    private const double Offset = 4096;
    private const int Tile = 1024;

    [Fact]
    public void WorldToPixel_matches_web_mapPixel()
    {
        var (px, py) = MapProjection.WorldToPixel(Offset, Offset, Scale, 125146.2265625, 176543.71875);
        Assert.Equal(Offset + Scale * 125146.2265625, px, 9);
        Assert.Equal(Offset + Scale * 176543.71875, py, 9);
    }

    [Fact]
    public void PixelToWorld_is_inverse_of_WorldToPixel()
    {
        var (px, py) = MapProjection.WorldToPixel(Offset, Offset, Scale, 100_000, 200_000);
        var (x, y) = MapProjection.PixelToWorld(Offset, Offset, Scale, px, py);
        Assert.Equal(100_000, x, 6);
        Assert.Equal(200_000, y, 6);
    }

    [Fact]
    public void TileWorldRect_matches_web_drawZoneMap_corners()
    {
        var (x0, y0, x1, y1) = MapProjection.TileWorldRect(Offset, Offset, Scale, Tile, 5, 5);
        Assert.Equal((5 * Tile - Offset) / Scale, x0, 9);
        Assert.Equal((5 * Tile - Offset) / Scale, y0, 9);
        Assert.Equal((6 * Tile - Offset) / Scale, x1, 9);
        Assert.Equal((6 * Tile - Offset) / Scale, y1, 9);

        // The tile's top-left pixel maps back to (x0, y0).
        var (px, py) = MapProjection.WorldToPixel(Offset, Offset, Scale, x0, y0);
        Assert.Equal(5 * Tile, px, 6);
        Assert.Equal(5 * Tile, py, 6);
    }

    [Fact]
    public void Nudge_shifts_only_the_image_plane_not_packet_markers()
    {
        // A negative offsetX / positive offsetY nudge (seen in a stale Altgard align) moves the bitmap SW
        // relative to markers that still use raw world centimeters.
        const double nudgeX = -27.25801245848088;
        const double nudgeY = 12.918486300943165;
        var playerX = 125146.2265625;
        var playerY = 176543.71875;

        var (px0, py0) = MapProjection.WorldToPixel(Offset, Offset, Scale, playerX, playerY);
        var (px1, py1) = MapProjection.WorldToPixel(Offset + nudgeX, Offset + nudgeY, Scale, playerX, playerY);

        // Image pixel under the player moves: smaller X (west), larger Y (south with north = -y).
        Assert.True(px1 < px0);
        Assert.True(py1 > py0);
        var westCm = (px0 - px1) / Scale;
        var southCm = (py1 - py0) / Scale;
        Assert.InRange(westCm, 2700, 2750);
        Assert.InRange(southCm, 1280, 1300);
    }
}
