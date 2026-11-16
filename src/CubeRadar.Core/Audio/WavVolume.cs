using System.Buffers.Binary;

namespace CubeRadar.Core.Audio;

/// <summary>
/// The radar's sound at an adjustable volume, as WAV bytes: the built-in two-tone beep is generated, a user .wav has
/// its samples scaled. Pure byte work; playing is up to the caller.
/// </summary>
public static class WavVolume
{
    public const int SampleRate = 44_100;

    /// <summary>Peak of the cube discovery chime at 100 %.</summary>
    private const double BeepPeak = 0.48;

    /// <summary>Volume 0–100 % to an amplitude factor. Squared, so the slider feels even to the ear
    /// (50 % ≈ −12 dB) instead of only changing near the bottom.</summary>
    public static double Gain(int volumePct) => Math.Pow(Math.Clamp(volumePct, 0, 100) / 100.0, 2);

    /// <summary>New-cube discovery chime: a warm, sparkling stereo FM bell arpeggio (Dmaj9: D5 → A5 → E6 → F#6 with a
    /// soft D4 body and a diffused stereo tail), clearly distinct from the taut threat lock in <see cref="EnemyAlert"/>.
    /// 16-bit stereo, about 0.62 s.</summary>
    public static byte[] Beep(int volumePct)
    {
        var frames = SampleRate * 620 / 1000;
        var dryL = new double[frames];
        var dryR = new double[frames];
        var sendL = new double[frames];
        var sendR = new double[frames];

        // Soft warm root cushion + crisp crystal tap at the start.
        FmVoice(dryL, dryR, sendL, sendR, 0, 320, 293.66, 0.14, pan: 0.0, idx2: 0.55, idx3: 0.08, decayS: 0.14, reverbSend: 0.30);
        Click(dryL, dryR, sendL, sendR, startMs: 0, hz: 4200, amp: 0.055, pan: -0.20);

        // Sparkling upward Dmaj9 bell cascade across the stereo field (D5 → A5 → E6 → F#6).
        FmVoice(dryL, dryR, sendL, sendR, 0, 380, Cents(587.33, -2.5), 0.24, pan: -0.32, idx2: 1.20, idx3: 0.32, decayS: 0.165, reverbSend: 0.48);
        FmVoice(dryL, dryR, sendL, sendR, 58, 390, Cents(880.00, +2.5), 0.23, pan: +0.26, idx2: 1.15, idx3: 0.28, decayS: 0.175, reverbSend: 0.54);
        FmVoice(dryL, dryR, sendL, sendR, 118, 420, Cents(1318.51, -3.0), 0.21, pan: -0.22, idx2: 1.05, idx3: 0.24, decayS: 0.195, reverbSend: 0.62);
        FmVoice(dryL, dryR, sendL, sendR, 119, 420, Cents(1318.51, +3.0), 0.17, pan: +0.34, idx2: 0.95, idx3: 0.20, decayS: 0.195, reverbSend: 0.62);
        FmVoice(dryL, dryR, sendL, sendR, 176, 410, 1479.98, 0.175, pan: +0.16, idx2: 0.85, idx3: 0.18, decayS: 0.205, reverbSend: 0.68);
        // Subtle high harmonic shimmer on A6.
        FmVoice(dryL, dryR, sendL, sendR, 178, 260, 1760.00, 0.055, pan: -0.30, idx2: 0.65, idx3: 0.12, decayS: 0.095, reverbSend: 0.75);

        return RenderStereo(dryL, dryR, sendL, sendR, wetMix: 0.28, BeepPeak, volumePct);
    }

    /// <summary>First enemy after an empty view. A two-step stereo HUD threat lock (E4 pulse → B4/E5/F#5 glassy FM
    /// lock with a tight sub-transient and a short diffused stereo tail), without any retro sine glide. 16-bit stereo,
    /// about 0.52 s.</summary>
    public static byte[] EnemyAlert(int volumePct)
    {
        var frames = SampleRate * 520 / 1000;
        var dryL = new double[frames];
        var dryR = new double[frames];
        var sendL = new double[frames];
        var sendR = new double[frames];

        // Tight sub-membrane transient on both steps (dry only, so the space tail stays clean).
        SubKick(dryL, dryR, startMs: 0, ms: 38, hzTop: 142, hzFloor: 60, amp: 0.24);
        SubKick(dryL, dryR, startMs: 86, ms: 44, hzTop: 156, hzFloor: 65, amp: 0.20);

        // Tactile micro-clicks and a brief stereo air pull into the lock note.
        Click(dryL, dryR, sendL, sendR, startMs: 0, hz: 2700, amp: 0.09, pan: -0.15);
        AirPull(dryL, dryR, sendL, sendR, startMs: 62, ms: 26, amp: 0.075);
        Click(dryL, dryR, sendL, sendR, startMs: 86, hz: 3400, amp: 0.12, pan: 0.18);

        // Step 1 (0 ms): muted taut FM pulse on E4, stereo micro-detuned.
        FmVoice(dryL, dryR, sendL, sendR, 0, 150, Cents(329.63, -3.5), 0.18, pan: -0.28, idx2: 1.55, idx3: 0.45, decayS: 0.052, reverbSend: 0.35);
        FmVoice(dryL, dryR, sendL, sendR, 0, 150, Cents(329.63, +3.5), 0.18, pan: +0.28, idx2: 1.55, idx3: 0.45, decayS: 0.052, reverbSend: 0.35);

        // Step 2 (86 ms): crisp open-fifth + octave + 9th threat lock (B4 + E5 + F#5 + faint B5 glass harmonic).
        FmVoice(dryL, dryR, sendL, sendR, 86, 330, 493.88, 0.22, pan: -0.22, idx2: 1.85, idx3: 0.60, decayS: 0.115, reverbSend: 0.50);
        FmVoice(dryL, dryR, sendL, sendR, 86, 350, Cents(659.25, -4.0), 0.20, pan: -0.36, idx2: 2.15, idx3: 0.55, decayS: 0.130, reverbSend: 0.55);
        FmVoice(dryL, dryR, sendL, sendR, 87, 350, Cents(659.25, +4.0), 0.20, pan: +0.36, idx2: 2.15, idx3: 0.55, decayS: 0.130, reverbSend: 0.55);
        FmVoice(dryL, dryR, sendL, sendR, 88, 290, 739.99, 0.105, pan: +0.30, idx2: 1.35, idx3: 0.30, decayS: 0.095, reverbSend: 0.65);
        FmVoice(dryL, dryR, sendL, sendR, 86, 180, 987.77, 0.065, pan: -0.26, idx2: 1.10, idx3: 0.25, decayS: 0.048, reverbSend: 0.70);

        return RenderStereo(dryL, dryR, sendL, sendR, wetMix: 0.24, targetPeak: 0.42, volumePct);
    }

    private static byte[] RenderStereo(double[] dryL, double[] dryR, double[] sendL, double[] sendR,
        double wetMix, double targetPeak, int volumePct)
    {
        var frames = dryL.Length;
        var wetL = Diffuse(sendL, stereoOffset: 0);
        var wetR = Diffuse(sendR, stereoOffset: 23);

        var peak = 0.0;
        var fadeLen = SampleRate * 35 / 1000;
        for (var i = 0; i < frames; i++)
        {
            var tailFade = i >= frames - fadeLen ? (frames - 1 - i) / (double)fadeLen : 1.0;
            dryL[i] = (dryL[i] + wetL[i] * wetMix) * tailFade;
            dryR[i] = (dryR[i] + wetR[i] * wetMix) * tailFade;
            peak = Math.Max(peak, Math.Max(Math.Abs(dryL[i]), Math.Abs(dryR[i])));
        }

        var scale = (peak > 1e-9 ? targetPeak / peak : 0.0) * Gain(volumePct) * short.MaxValue;
        var interleaved = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            interleaved[2 * i] = (short)Math.Clamp(Math.Round(dryL[i] * scale), short.MinValue, short.MaxValue);
            interleaved[2 * i + 1] = (short)Math.Clamp(Math.Round(dryR[i] * scale), short.MinValue, short.MaxValue);
        }
        return Pcm(interleaved, channels: 2);
    }

    private static double Cents(double hz, double cents) => hz * Math.Pow(2.0, cents / 1200.0);

    /// <summary>Two-modulator FM voice: a fast-decaying metallic/glassy attack (ratios 2× and 3.01×) that settles into
    /// a clean fundamental within ~20 ms.</summary>
    private static void FmVoice(double[] dryL, double[] dryR, double[] sendL, double[] sendR,
        int startMs, int ms, double hz, double amp, double pan, double idx2, double idx3, double decayS, double reverbSend)
    {
        var start = SampleRate * startMs / 1000;
        var n = SampleRate * ms / 1000;
        var attack = Math.Max(1, SampleRate * 2 / 1000);
        var release = Math.Max(1, SampleRate * 16 / 1000);
        var (gL, gR) = Pan(pan);
        for (var i = 0; i < n && start + i < dryL.Length; i++)
        {
            var t = i / (double)SampleRate;
            var attackEnv = i < attack ? 0.5 * (1 - Math.Cos(Math.PI * i / attack)) : 1.0;
            var releaseEnv = i > n - release ? (n - 1 - i) / (double)release : 1.0;
            var env = attackEnv * releaseEnv * Math.Exp(-t / decayS);

            var mod = idx2 * Math.Exp(-t / 0.017) * Math.Sin(2 * Math.PI * hz * 2.0 * t)
                    + idx3 * Math.Exp(-t / 0.008) * Math.Sin(2 * Math.PI * hz * 3.01 * t);
            var s = Math.Sin(2 * Math.PI * hz * t + mod) * env * amp;

            dryL[start + i] += s * gL;
            dryR[start + i] += s * gR;
            sendL[start + i] += s * gL * reverbSend;
            sendR[start + i] += s * gR * reverbSend;
        }
    }

    /// <summary>Short pitched membrane transient under the attack (no slow boom).</summary>
    private static void SubKick(double[] dryL, double[] dryR, int startMs, int ms, double hzTop, double hzFloor, double amp)
    {
        var start = SampleRate * startMs / 1000;
        var n = SampleRate * ms / 1000;
        var attack = Math.Max(1, SampleRate * 2 / 1000);
        double phase = 0;
        for (var i = 0; i < n && start + i < dryL.Length; i++)
        {
            var t = i / (double)SampleRate;
            var hz = hzFloor + (hzTop - hzFloor) * Math.Exp(-t / 0.006);
            phase += hz / SampleRate;
            var fade = i < attack ? i / (double)attack : 1.0;
            var s = Math.Sin(2 * Math.PI * phase) * fade * Math.Exp(-t / 0.013) * amp;
            dryL[start + i] += s;
            dryR[start + i] += s;
        }
    }

    /// <summary>Ultra-short focused transient tick (~5 ms) for crisp HUD definition at low volume.</summary>
    private static void Click(double[] dryL, double[] dryR, double[] sendL, double[] sendR, int startMs, double hz, double amp, double pan)
    {
        var start = SampleRate * startMs / 1000;
        var n = SampleRate * 7 / 1000;
        var (gL, gR) = Pan(pan);
        for (var i = 0; i < n && start + i < dryL.Length; i++)
        {
            var t = i / (double)SampleRate;
            var s = Math.Sin(2 * Math.PI * hz * t) * Math.Exp(-t / 0.0016) * amp;
            dryL[start + i] += s * gL;
            dryR[start + i] += s * gR;
            sendL[start + i] += s * gL * 0.25;
            sendR[start + i] += s * gR * 0.25;
        }
    }

    /// <summary>Brief filtered reverse-air sweep right before the second hit so the two steps feel connected.</summary>
    private static void AirPull(double[] dryL, double[] dryR, double[] sendL, double[] sendR, int startMs, int ms, double amp)
    {
        var start = SampleRate * startMs / 1000;
        var n = SampleRate * ms / 1000;
        uint rng = 0x9E3779B9;
        double lp = 0, hp = 0;
        for (var i = 0; i < n && start + i < dryL.Length; i++)
        {
            rng = rng * 1664525u + 1013904223u;
            var noise = ((rng >> 8) & 0xFFFFFF) / (double)0xFFFFFF * 2 - 1;
            var u = n == 1 ? 1.0 : i / (double)(n - 1);
            lp += (0.14 + 0.42 * u) * (noise - lp);
            hp += 0.08 * (lp - hp);
            var env = u * u * Math.Sin(Math.PI * u);
            var (gL, gR) = Pan(-0.35 + 0.70 * u);
            var s = (lp - hp) * env * amp;
            dryL[start + i] += s * gL;
            dryR[start + i] += s * gR;
            sendL[start + i] += s * gL * 0.4;
            sendR[start + i] += s * gR * 0.4;
        }
    }

    private static (double L, double R) Pan(double pan)
    {
        var angle = (Math.Clamp(pan, -1.0, 1.0) + 1.0) * (Math.PI * 0.25);
        return (Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>Compact Schroeder plate (4 damped parallel combs into 2 series allpasses); <paramref name="stereoOffset"/>
    /// decorrelates the right channel from the left.</summary>
    private static double[] Diffuse(double[] input, int stereoOffset)
    {
        ReadOnlySpan<int> combDelays = [1116 + stereoOffset, 1188 + stereoOffset, 1277 + stereoOffset, 1356 + stereoOffset];
        var mixed = new double[input.Length];
        foreach (var delay in combDelays)
        {
            var ring = new double[delay];
            var idx = 0;
            var filterStore = 0.0;
            for (var i = 0; i < input.Length; i++)
            {
                var delayed = ring[idx];
                filterStore = delayed * 0.62 + filterStore * 0.38;
                ring[idx] = input[i] + filterStore * 0.64;
                if (++idx == delay) idx = 0;
                mixed[i] += delayed * 0.25;
            }
        }

        ReadOnlySpan<int> allpassDelays = [556 + stereoOffset, 441 + stereoOffset];
        foreach (var delay in allpassDelays)
        {
            var ring = new double[delay];
            var idx = 0;
            for (var i = 0; i < mixed.Length; i++)
            {
                var bufOut = ring[idx];
                var x = mixed[i];
                mixed[i] = -x + bufOut;
                ring[idx] = x + bufOut * 0.5;
                if (++idx == delay) idx = 0;
            }
        }
        return mixed;
    }

    private static byte[] Pcm(List<short> samples) => Pcm(samples.ToArray(), channels: 1);

    private static byte[] Pcm(ReadOnlySpan<short> samples, short channels)
    {
        var blockAlign = (short)(channels * 2);
        var data = samples.Length * 2;
        var wav = new byte[44 + data];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], SampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], data);
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(s[(44 + 2 * i)..], samples[i]);
        return wav;
    }

    /// <summary>Where a .wav keeps its format (the WAVEFORMATEX in the "fmt " chunk) and its samples.</summary>
    public readonly record struct Layout(int FormatOffset, int FormatLength, int DataOffset, int DataLength);

    /// <summary>The <see cref="Layout"/> of a .wav, or null if it is not a WAV file.</summary>
    public static Layout? Find(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8)) return null;
        int fmtAt = -1, fmtSize = 0;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = wav.Slice(at, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav[(at + 4)..]), (uint)(wav.Length - at - 8));
            if (id.SequenceEqual("fmt "u8) && size >= 16) (fmtAt, fmtSize) = (at + 8, size);
            else if (id.SequenceEqual("data"u8)) return fmtAt < 0 ? null : new Layout(fmtAt, fmtSize, at + 8, size);
            at += 8 + size + (size & 1);
        }
        return null;
    }

    public enum ScaleResult { Scaled, NotWav, UnsupportedFormat }

    /// <summary>Scales the samples of a .wav in place. PCM 8/16/24/32 bit and 32-bit float (also as
    /// WAVE_FORMAT_EXTENSIBLE); anything else (ADPCM, MP3 in WAV, …) is left as it is.</summary>
    public static ScaleResult Scale(byte[] wav, int volumePct)
    {
        if (Find(wav) is not { } w) return ScaleResult.NotWav;
        var fmt = wav.AsSpan(w.FormatOffset, w.FormatLength);
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt), bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (tag == 0xFFFE && fmt.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // sub-format GUID starts with the tag
        return ScaleSamples(wav.AsSpan(w.DataOffset, w.DataLength), tag, bits, Gain(volumePct))
            ? ScaleResult.Scaled
            : ScaleResult.UnsupportedFormat;
    }

    private static bool ScaleSamples(Span<byte> data, int tag, int bits, double gain)
    {
        switch (tag, bits)
        {
            case (1, 8): // unsigned around 128
                for (var i = 0; i < data.Length; i++) data[i] = (byte)Math.Clamp(Math.Round((data[i] - 128) * gain) + 128, 0, 255);
                return true;
            case (1, 16):
                for (var i = 0; i + 2 <= data.Length; i += 2)
                    BinaryPrimitives.WriteInt16LittleEndian(data[i..], (short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(data[i..]) * gain));
                return true;
            case (1, 24):
                for (var i = 0; i + 3 <= data.Length; i += 3)
                {
                    var v = (int)Math.Round(((data[i] | data[i + 1] << 8 | (sbyte)data[i + 2] << 16)) * gain);
                    data[i] = (byte)v;
                    data[i + 1] = (byte)(v >> 8);
                    data[i + 2] = (byte)(v >> 16);
                }
                return true;
            case (1, 32):
                for (var i = 0; i + 4 <= data.Length; i += 4)
                    BinaryPrimitives.WriteInt32LittleEndian(data[i..], (int)Math.Round(BinaryPrimitives.ReadInt32LittleEndian(data[i..]) * gain));
                return true;
            case (3, 32):
                for (var i = 0; i + 4 <= data.Length; i += 4)
                    BinaryPrimitives.WriteSingleLittleEndian(data[i..], (float)(BinaryPrimitives.ReadSingleLittleEndian(data[i..]) * gain));
                return true;
            default:
                return false;
        }
    }
}
