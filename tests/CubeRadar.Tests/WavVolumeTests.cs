using System.Buffers.Binary;
using CubeRadar.Core.Audio;

namespace CubeRadar.Tests;

public class WavVolumeTests
{
    private static short MaxSample(byte[] wav)
    {
        short max = 0;
        for (var i = 44; i + 2 <= wav.Length; i += 2) max = Math.Max(max, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i))));
        return max;
    }

    [Fact]
    public void Beep_is_a_valid_pcm_wav_whose_loudness_follows_the_volume()
    {
        var full = WavVolume.Beep(100);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(full, 0, 4));
        Assert.Equal(full.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(full.AsSpan(22))); // stereo
        Assert.Equal(WavVolume.SampleRate * 620 / 1000 * 4, BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(40)));

        var peak = MaxSample(full);
        Assert.InRange(peak, 0.46 * short.MaxValue, 0.50 * short.MaxValue);
        Assert.InRange(MaxSample(WavVolume.Beep(50)), peak * 0.24, peak * 0.26);
        Assert.True(MaxSample(WavVolume.Beep(10)) < MaxSample(WavVolume.Beep(20)));
    }

    [Fact]
    public void Enemy_alert_is_a_stereo_wav_and_follows_the_volume()
    {
        var full = WavVolume.EnemyAlert(100);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(full, 0, 4));
        Assert.Equal(full.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(full.AsSpan(22))); // stereo
        Assert.Equal(WavVolume.SampleRate * 520 / 1000 * 4, BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(40)));

        var stereoDiff = 0L;
        for (var i = 44; i + 4 <= full.Length; i += 4)
            stereoDiff += Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(full.AsSpan(i)) - BinaryPrimitives.ReadInt16LittleEndian(full.AsSpan(i + 2)));
        Assert.True(stereoDiff > 0);

        var peak = MaxSample(full);
        Assert.InRange(peak, 0.40 * short.MaxValue, 0.44 * short.MaxValue);
        Assert.InRange(MaxSample(WavVolume.EnemyAlert(50)), peak * 0.24, peak * 0.26);
    }

    [Fact]
    public void Scale_reaches_the_samples_behind_other_chunks_and_formats()
    {
        // A user .wav: 16-bit PCM with a LIST chunk (odd length, padded) before the data
        var wav = WavVolume.Beep(100).ToList();
        byte[] list = [.. "LIST"u8, 3, 0, 0, 0, 1, 2, 3, 0];
        wav.InsertRange(36, list);
        var bytes = wav.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        var before = MaxSample(WavVolume.Beep(100));

        Assert.Equal(WavVolume.ScaleResult.Scaled, WavVolume.Scale(bytes, 50));
        short max = 0;
        for (var i = 44 + list.Length; i + 2 <= bytes.Length; i += 2)
            max = Math.Max(max, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i))));
        Assert.InRange(max, before * 0.24, before * 0.26);
        Assert.Equal(1, bytes[36 + 8]); // the LIST chunk is untouched
    }

    [Fact]
    public void Scale_handles_8_bit_and_float_and_leaves_unknown_formats()
    {
        static byte[] Wav(ushort tag, ushort bits, byte[] data)
        {
            var w = WavVolume.Beep(100)[..44];
            BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(20), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(w.AsSpan(34), bits);
            BinaryPrimitives.WriteInt32LittleEndian(w.AsSpan(40), data.Length);
            return [.. w, .. data];
        }

        var eight = Wav(1, 8, [128, 255, 0]);
        Assert.Equal(WavVolume.ScaleResult.Scaled, WavVolume.Scale(eight, 50));
        Assert.Equal([128, 160, 96], eight[44..]); // ±127/128 around the centre, × 0.25

        var f = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(f, 0.8f);
        var flt = Wav(3, 32, f);
        Assert.Equal(WavVolume.ScaleResult.Scaled, WavVolume.Scale(flt, 50));
        Assert.Equal(0.2f, BinaryPrimitives.ReadSingleLittleEndian(flt.AsSpan(44)), 5);

        var adpcm = Wav(2, 4, [1, 2, 3, 4]);
        Assert.Equal(WavVolume.ScaleResult.UnsupportedFormat, WavVolume.Scale(adpcm, 50));
        Assert.Equal([1, 2, 3, 4], adpcm[44..]);

        Assert.Equal(WavVolume.ScaleResult.NotWav, WavVolume.Scale("ID3 not a wav file"u8.ToArray(), 50));
    }
}
