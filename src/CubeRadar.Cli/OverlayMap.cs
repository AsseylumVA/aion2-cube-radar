using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using CubeRadar.Core.Radar;

namespace CubeRadar.Cli;

/// <summary>
/// The same zone picture the web radar draws, clipped to the overlay disc. Only the background: live cubes stay the
/// colored markers drawn by <see cref="OverlayRenderer"/>.
/// </summary>
internal static class OverlayMap
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, Bitmap?> Tiles = new();
    private static ZoneFile[]? _zones;

    public static void Draw(Graphics g, RadarSnapshot snapshot, double playerX, double playerY, Func<double, double, PointF> toScreen,
        float cx, float cy, float radius, IReadOnlyDictionary<string, MapNudge>? nudges)
    {
        var zone = Find(snapshot);
        if (zone is null) return;
        var shift = nudges is not null && nudges.TryGetValue(zone.Id, out var nudge) ? nudge : null;
        var offsetX = zone.OffsetX + (shift?.X ?? 0);
        var offsetY = zone.OffsetY + (shift?.Y ?? 0);
        var reach = radius / Math.Max(1e-6, Distance(toScreen, playerX, playerY));

        var saved = g.Save();
        try
        {
            using var clip = new GraphicsPath();
            clip.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2);
            g.SetClip(clip);
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
