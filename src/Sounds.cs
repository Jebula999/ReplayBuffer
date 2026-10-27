using System;
using System.IO;
using System.Media;

namespace ReplayBuffer;

/// <summary>Short generated chimes: rising = saved, falling = failed.</summary>
static class Sounds
{
    static readonly byte[] Ok = Chime(new[] { 880.0, 1320.0 }, 0.09);
    static readonly byte[] Fail = Chime(new[] { 330.0, 220.0 }, 0.15);

    public static void Play(bool success, bool enabled)
    {
        if (!enabled) return;
        try { new SoundPlayer(new MemoryStream(success ? Ok : Fail)).Play(); } catch (Exception) { }
    }

    static byte[] Chime(double[] freqs, double dur)
    {
        const int rate = 44100;
        int per = (int)(rate * dur);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int data = freqs.Length * per * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (double f in freqs)
            for (int i = 0; i < per; i++)
            {
                double env = Math.Min(1.0, i / 300.0) * (1.0 - (double)i / per);
                w.Write((short)(9000 * env * Math.Sin(2 * Math.PI * f * i / rate)));
            }
        return ms.ToArray();
    }
}
