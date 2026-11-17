namespace CubeRadar.Core.Radar;

/// <summary>
/// Zone-image projection. Same math as <c>wwwroot/app.js</c> <c>mapPixel</c> / <c>drawZoneMap</c>:
/// image +x is world +x, image +y is world +y; pixel = offset + scale × world_cm.
/// </summary>
public static class MapProjection
{
    /// <summary>Calibration id for the 816 km square fit (v0.9.7+). Matches web <c>mapFit</c>.</summary>
    public const string Fit816 = "816";

    /// <summary>World centimeters → pixels on the stitched zone image.</summary>
    public static (double Px, double Py) WorldToPixel(double offsetX, double offsetY, double scale, double x, double y) =>
        (offsetX + scale * x, offsetY + scale * y);

    /// <summary>Pixels on the zone image → world centimeters.</summary>
    public static (double X, double Y) PixelToWorld(double offsetX, double offsetY, double scale, double px, double py) =>
        ((px - offsetX) / scale, (py - offsetY) / scale);

    /// <summary>World-cm rectangle of the tile at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public static (double X0, double Y0, double X1, double Y1) TileWorldRect(
        double offsetX, double offsetY, double scale, int tile, int col, int row)
    {
        var x0 = (col * tile - offsetX) / scale;
        var y0 = (row * tile - offsetY) / scale;
        var x1 = ((col + 1) * tile - offsetX) / scale;
        var y1 = ((row + 1) * tile - offsetY) / scale;
        return (x0, y0, x1, y1);
    }
}
