using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ReplayBuffer;

/// <summary>
/// Keeps the last N seconds of ffmpeg's MPEG-TS output, split into chunks that each start on a video keyframe
/// (one every 2 s), so any run of chunks plays from its first frame. Chunks live in RAM, or in temp files
/// when a disk folder is given.
/// </summary>
public sealed class ClipRing
{
    const int Packet = 188;
    const int VideoPid = 0x100, PmtPid = 0x1000;  // fixed via -mpegts_start_pid / -mpegts_pmt_start_pid

    sealed class Chunk
    {
        public double Start;
        public Stream Data = null!;
        public string? File;
        public long Length;
    }

    readonly object gate = new();
    readonly LinkedList<Chunk> chunks = new();
    readonly double keepSeconds;
    readonly string? diskDir;
    int fileNo;
    byte[]? pat, pmt;

    public ClipRing(int bufferSeconds, string? diskDir)
    {
        keepSeconds = bufferSeconds + 4;
        this.diskDir = diskDir;
        if (diskDir != null) Directory.CreateDirectory(diskDir);
    }

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Reads the stream until it ends (runs on its own thread).</summary>
    public void Fill(Stream src)
    {
        var buf = new byte[Packet * 1024];
        int have = 0;
        try
        {
            for (int n; (n = src.Read(buf, have, buf.Length - have)) > 0;)
            {
                have += n;
                int pos = 0;
                while (have - pos >= Packet)
                {
                    if (buf[pos] != 0x47) { pos++; continue; }  // resync
                    Add(buf.AsSpan(pos, Packet));
                    pos += Packet;
                }
                Buffer.BlockCopy(buf, pos, buf, 0, have - pos);
                have -= pos;
            }
        }
        catch (Exception) { }  // ffmpeg stopped
        finally
        {
            lock (gate)
                while (chunks.Count > 0) Drop();
        }
    }

    void Add(ReadOnlySpan<byte> p)
    {
        int pid = ((p[1] & 0x1f) << 8) | p[2];
        bool unitStart = (p[1] & 0x40) != 0;
        bool keyframe = pid == VideoPid && unitStart && (p[3] & 0x20) != 0 && p[4] > 0 && (p[5] & 0x40) != 0;
        lock (gate)
        {
            if (pid == 0 && unitStart) pat ??= p.ToArray();
            else if (pid == PmtPid && unitStart) pmt ??= p.ToArray();
            if (keyframe || chunks.Count == 0)
            {
                chunks.Last?.Value.Data.Flush();
                chunks.AddLast(NewChunk());
                // drop chunks that have fallen entirely out of the buffer window
                while (chunks.Count > 1 && chunks.First!.Next!.Value.Start < Now - keepSeconds) Drop();
            }
            Chunk c = chunks.Last!.Value;
            c.Data.Write(p);
            c.Length += p.Length;
        }
    }

    Chunk NewChunk()
    {
        if (diskDir == null) return new Chunk { Start = Now, Data = new MemoryStream(4 << 20) };
        string f = Path.Combine(diskDir, $"{fileNo++:D6}.ts");
        return new Chunk
        {
            Start = Now, File = f,
            Data = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 20),
        };
    }

    void Drop()
    {
        Chunk c = chunks.First!.Value;
        chunks.RemoveFirst();
        c.Data.Dispose();
        if (c.File != null) try { File.Delete(c.File); } catch (Exception) { }  // open readers keep their copy
    }

    /// <summary>
    /// Stream header plus every chunk covering the last <paramref name="seconds"/>, up to now. The caller reads
    /// and disposes the streams; chunks dropped from the ring meanwhile stay readable.
    /// </summary>
    public List<Stream> Snapshot(int seconds)
    {
        lock (gate)
        {
            var list = new List<Stream>();
            if (pat != null && pmt != null) list.Add(new MemoryStream([.. pat, .. pmt]));
            double from = Now - seconds;
            LinkedListNode<Chunk>? first = chunks.First;
            // start at the last keyframe at or before the window start, so the clip is at least `seconds` long
            for (var n = chunks.First; n != null && n.Value.Start <= from; n = n.Next) first = n;
            for (var n = first; n != null; n = n.Next)
            {
                Chunk c = n.Value;
                if (c.Length == 0) continue;
                if (c.File == null)
                {
                    var ms = (MemoryStream)c.Data;
                    // finished chunks never change again, so they're shared; only the one still growing is copied
                    list.Add(n.Next == null ? new MemoryStream(ms.ToArray()) : new MemoryStream(ms.GetBuffer(), 0, (int)c.Length, false));
                }
                else
                {
                    c.Data.Flush();
                    var f = new FileStream(c.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    list.Add(n.Next == null ? Copy(f, c.Length) : f);  // the growing one: only what's written so far
                }
            }
            return list;
        }
    }

    static MemoryStream Copy(FileStream f, long length)
    {
        using (f)
        {
            var b = new byte[length];
            f.ReadExactly(b);
            return new MemoryStream(b);
        }
    }
}
