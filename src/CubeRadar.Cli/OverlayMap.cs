using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using CubeRadar.Core.Radar;

namespace CubeRadar.Cli;

/// <summary>
/// The same zone picture the web radar draws, filling the square reserved for the map, plus the static marks from
/// <c>spawns.json</c> / <c>flight-rings.json</c> / <c>kibelisks.json</c>. Live cubes stay the colored markers drawn by
/// <see cref="OverlayRenderer"/>.
/// </summary>
internal static class OverlayMap
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, Bitmap?> Tiles = new();
    private static readonly Dictionary<string, Bitmap?> Icons = new();
    private static ZoneFile[]? _zones;
    private static Dictionary<string, PointF[]>? _spawns;
    private static Dictionary<string, PointF[]>? _flightRings;
    private static Dictionary<string, Mark[]>? _kibelisks;
    private const float LiveCubeClearance = 300; // cm; same cutoff as the web map
    private readonly record struct Mark(float X, float Y, string? Icon);

    public static void Draw(Graphics g, RadarSnapshot snapshot, double playerX, double playerY, Func<double, double, PointF> toScreen,
        RectangleF area, IReadOnlyDictionary<string, MapNudge>? nudges)
    {
        var zone = Find(snapshot);
        if (zone is null) return;
        var shift = nudges is not null && nudges.TryGetValue(zone.Id, out var nudge) ? nudge : null;
        var offsetX = zone.OffsetX + (shift?.X ?? 0);
        var offsetY = zone.OffsetY + (shift?.Y ?? 0);
        // The square's corner is farther out than its edge, so the tile search uses that diagonal.
        var reach = Math.Sqrt(area.Width * area.Width + area.Height * area.Height) / 2
            / Math.Max(1e-6, Distance(toScreen, playerX, playerY));

        var saved = g.Save();
        try
        {
            g.SetClip(area);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            for (var row = 0; row < zone.TilesY; row++)
            for (var col = 0; col < zone.TilesX; col++)
            {
                var x0 = (col * zone.Tile - offsetX) / zone.Scale;
                var y0 = (row * zone.Tile - offsetY) / zone.Scale;
                var x1 = ((col + 1) * zone.Tile - offsetX) / zone.Scale;
                var y1 = ((row + 1) * zone.Tile - offsetY) / zone.Scale;
                if (!Hits(x0, y0, x1, y1, playerX, playerY, reach)) continue;
                var tile = LoadTile(zone, col, row);
                if (tile is null) continue;
                var origin = toScreen(x0, y0);
                var right = toScreen(x1, y0);
                var down = toScreen(x0, y1);
                g.DrawImage(tile, new[] { origin, right, down });
            }
        }
        finally
        {
            g.Restore(saved);
        }
    }

    /// <summary>Static web-map marks inside the radar disc. Spawn marks next to a live cube are skipped.</summary>
    public static void DrawMarks(Graphics g, RadarSnapshot snapshot, Func<double, double, PointF> toScreen,
        PointF center, float radius, bool showCubes, bool showFlightRings, bool showKibelisks)
    {
        if (!showCubes && !showFlightRings && !showKibelisks) return;
        var zone = Find(snapshot);
        if (zone is null) return;
        var saved = g.Save();
        try
        {
            using var clip = new GraphicsPath();
            clip.AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2);
            g.SetClip(clip);
            if (showCubes)
            {
                var icon = LoadIcon("maps/hidden-cube.webp");
                foreach (var p in Points(Spawns(), zone.Id))
                {
                    if (snapshot.Cubes.Any(c => Dist2(c.X, c.Y, p.X, p.Y) < LiveCubeClearance * LiveCubeClearance)) continue;
                    var s = toScreen(p.X, p.Y);
                    if (!Inside(s, center, radius - 4)) continue;
                    DrawIcon(g, icon, s, 13f, Color.FromArgb(180, 231, 215, 162));
                }
            }
            if (showFlightRings)
            {
                var icon = LoadIcon("maps/icons/flight-ring.webp");
                foreach (var p in Points(FlightRings(), zone.Id))
                {
                    var s = toScreen(p.X, p.Y);
                    if (!Inside(s, center, radius - 4)) continue;
                    DrawIcon(g, icon, s, 17f, Color.FromArgb(200, 130, 196, 255));
                }
            }
            if (showKibelisks)
            {
                foreach (var mark in Marks(Kibelisks(), zone.Id))
                {
                    var s = toScreen(mark.X, mark.Y);
                    if (!Inside(s, center, radius - 4)) continue;
                    var icon = LoadIcon(string.IsNullOrEmpty(mark.Icon) ? "maps/icons/abyss-portal.webp" : "maps/icons/" + mark.Icon);
                    DrawIcon(g, icon, s, 19f, Color.FromArgb(210, 94, 224, 197));
                }
            }
        }
        finally
        {
            g.Restore(saved);
        }
    }

    private static void DrawIcon(Graphics g, Bitmap? icon, PointF at, float size, Color fallback)
    {
        if (icon is not null)
        {
            g.DrawImage(icon, at.X - size / 2, at.Y - size / 2, size, size);
            return;
        }
        using var brush = new SolidBrush(fallback);
        g.FillRectangle(brush, at.X - size / 4, at.Y - size / 4, size / 2, size / 2);
    }

    private static bool Inside(PointF p, PointF c, float r)
    {
        var dx = p.X - c.X;
        var dy = p.Y - c.Y;
        return dx * dx + dy * dy <= r * r;
    }

    private static double Dist2(double x0, double y0, double x1, double y1)
    {
        var dx = x0 - x1;
        var dy = y0 - y1;
        return dx * dx + dy * dy;
    }

    private static IEnumerable<PointF> Points(Dictionary<string, PointF[]> catalog, string zoneId) =>
        catalog.TryGetValue(zoneId, out var list) ? list : [];

    private static IEnumerable<Mark> Marks(Dictionary<string, Mark[]> catalog, string zoneId) =>
        catalog.TryGetValue(zoneId, out var list) ? list : [];

    private static Dictionary<string, PointF[]> Spawns() => _spawns ??= LoadPoints("spawns.json");

    private static Dictionary<string, PointF[]> FlightRings() => _flightRings ??= LoadPoints("flight-rings.json");

    private static Dictionary<string, Mark[]> Kibelisks() => _kibelisks ??= LoadMarks("kibelisks.json");

    private static Dictionary<string, PointF[]> LoadPoints(string file)
    {
        try
        {
            var path = Path.Combine(AppPaths.WebRoot, "maps", file);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("zones", out var zones)) return new();
            var result = new Dictionary<string, PointF[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var zone in zones.EnumerateObject())
            {
                var list = new List<PointF>();
                foreach (var row in zone.Value.EnumerateArray())
                {
                    if (row.GetArrayLength() < 2) continue;
                    list.Add(new PointF((float)row[0].GetDouble(), (float)row[1].GetDouble()));
                }
                result[zone.Name] = list.ToArray();
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Rows are <c>[x, y, name, icon]</c>; the icon is the same webp the interactive map uses.</summary>
    private static Dictionary<string, Mark[]> LoadMarks(string file)
    {
        try
        {
            var path = Path.Combine(AppPaths.WebRoot, "maps", file);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("zones", out var zones)) return new();
            var result = new Dictionary<string, Mark[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var zone in zones.EnumerateObject())
            {
                var list = new List<Mark>();
                foreach (var row in zone.Value.EnumerateArray())
                {
                    if (row.GetArrayLength() < 2) continue;
                    var icon = row.GetArrayLength() >= 4 ? row[3].GetString() : null;
                    list.Add(new Mark((float)row[0].GetDouble(), (float)row[1].GetDouble(), icon));
                }
                result[zone.Name] = list.ToArray();
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private static Bitmap? LoadIcon(string relative)
    {
        var path = Path.Combine(AppPaths.WebRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (Icons.TryGetValue(path, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path)) bitmap = Webp.Decode(path);
        }
        catch (Exception ex) when (ex is IOException or COMException or ArgumentException or ExternalException)
        {
            bitmap = null;
        }
        Icons[path] = bitmap;
        return bitmap;
    }

    /// <summary>Pixels per world centimeter at the player, so the visible disc can be measured in centimeters.</summary>
    private static double Distance(Func<double, double, PointF> toScreen, double x, double y)
    {
        var a = toScreen(x, y);
        var b = toScreen(x + 100, y);
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy) / 100;
    }

    private static bool Hits(double x0, double y0, double x1, double y1, double px, double py, double radius)
    {
        var nx = Math.Clamp(px, Math.Min(x0, x1), Math.Max(x0, x1));
        var ny = Math.Clamp(py, Math.Min(y0, y1), Math.Max(y0, y1));
        var dx = px - nx;
        var dy = py - ny;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static ZoneFile? Find(RadarSnapshot snapshot)
    {
        var zones = Zones();
        if (snapshot.MapId is int id)
        {
            var byId = zones.FirstOrDefault(z => z.MapIds.Contains(id));
            if (byId is not null) return byId;
        }
        return string.IsNullOrEmpty(snapshot.Zone) ? null : zones.FirstOrDefault(z => z.Zone == snapshot.Zone);
    }

    private static ZoneFile[] Zones()
    {
        if (_zones is not null) return _zones;
        try
        {
            var path = Path.Combine(AppPaths.WebRoot, "maps", "maps.json");
            var file = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), Json);
            _zones = file?.Zones ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _zones = [];
        }
        return _zones;
    }

    private static Bitmap? LoadTile(ZoneFile zone, int col, int row)
    {
        var path = Path.Combine(AppPaths.WebRoot, zone.Path.Replace('/', Path.DirectorySeparatorChar),
            $"{zone.File}_{col:00}_{row:00}.webp");
        if (Tiles.TryGetValue(path, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path)) bitmap = Webp.Decode(path);
        }
        catch (Exception ex) when (ex is IOException or COMException or ArgumentException or ExternalException)
        {
            bitmap = null;
        }
        if (Tiles.Count > 16)
        {
            foreach (var old in Tiles.Values) old?.Dispose();
            Tiles.Clear();
        }
        Tiles[path] = bitmap;
        return bitmap;
    }

    private sealed class Catalog
    {
        public ZoneFile[] Zones { get; set; } = [];
    }

    private sealed class ZoneFile
    {
        public string Id { get; set; } = "";
        public string Zone { get; set; } = "";
        public int[] MapIds { get; set; } = [];
        public string File { get; set; } = "";
        public string Path { get; set; } = "";
        public int TilesX { get; set; }
        public int TilesY { get; set; }
        public int Tile { get; set; }
        public double Scale { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
    }
}

/// <summary>Decodes a WebP through Windows Imaging Component. GDI+ itself cannot read these tiles.</summary>
file static class Webp
{
    private static readonly Guid Format32Bgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    public static Bitmap Decode(string path)
    {
        // A Guid marked LPStruct is marshalled by value here and leaves the converter unusable. Pass it by reference.
        var factory = (IWICImagingFactory)new WicFactory();
        object? decoder = null;
        object? source = null;
        object? converter = null;
        try
        {
            decoder = factory.CreateDecoderFromFilename(path, IntPtr.Zero, 0x80000000, 0);
            source = ((IWICBitmapDecoder)decoder).GetFrame(0);
            converter = factory.CreateFormatConverter();
            var format = Format32Bgra;
            var converted = (IWICFormatConverter)converter;
            converted.Initialize((IWICBitmapSource)source, ref format, 0, IntPtr.Zero, 0, 0);
            converted.GetSize(out var width, out var height);
            var stride = (int)width * 4;
            var bytes = Marshal.AllocHGlobal(stride * (int)height);
            try
            {
                converted.CopyPixels(IntPtr.Zero, (uint)stride, (uint)(stride * height), bytes);
                var bitmap = new Bitmap((int)width, (int)height, PixelFormat.Format32bppArgb);
                var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[stride];
                    for (var y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(bytes + y * stride, row, 0, stride);
                        Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, stride);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                return bitmap;
            }
            finally
            {
                Marshal.FreeHGlobal(bytes);
            }
        }
        finally
        {
            Release(converter);
            Release(source);
            Release(decoder);
            Release(factory);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }
}

[ComImport]
[Guid("cacaf262-9370-4615-a13b-9f5539da4c0a")]
file class WicFactory;

[ComImport]
[Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
file interface IWICImagingFactory
{
    IWICBitmapDecoder CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr vendor, uint access, uint options);
    void SkipStream();
    void SkipHandle();
    void SkipComponentInfo();
    void SkipDecoder();
    void SkipEncoder();
    void SkipPalette();
    IWICFormatConverter CreateFormatConverter();
}

[ComImport]
[Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
file interface IWICBitmapDecoder
{
    void SkipCapability();
    void SkipInitialize();
    void SkipContainer();
    void SkipInfo();
    void SkipPalette();
    void SkipMetadata();
    void SkipPreview();
    void SkipColorContexts();
    void SkipThumbnail();
    void SkipFrameCount();
    IWICBitmapSource GetFrame(uint index);
}

[ComImport]
[Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
file interface IWICBitmapSource
{
    void GetSize(out uint width, out uint height);
    void GetPixelFormat(out Guid format);
    void GetResolution(out double dpiX, out double dpiY);
    void CopyPalette(IntPtr palette);
    void CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
}

[ComImport]
[Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
file interface IWICFormatConverter
{
    void GetSize(out uint width, out uint height);
    void GetPixelFormat(out Guid format);
    void GetResolution(out double dpiX, out double dpiY);
    void CopyPalette(IntPtr palette);
    void CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
    void Initialize(IWICBitmapSource source, ref Guid format, int dither, IntPtr palette, double alpha, int paletteType);
}
