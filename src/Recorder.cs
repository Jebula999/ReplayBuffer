using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReplayBuffer;

public sealed record AudioDevice(string Id, string Name, bool IsOutput);
public sealed record MonitorInfo(int Index, int Width, int Height, string Name);

/// <summary>
/// Runs the capture pipeline: capture.exe (GPU capture + hardware H.264) and one audio.exe per track feed
/// ffmpeg through named pipes; ffmpeg only muxes them into MPEG-TS on its stdout, which is kept in a RAM
/// ring (nothing is written to disk until a clip is saved). All sources stamp their data on the same
/// QueryPerformanceCounter clock, so tracks always line up.
/// </summary>
public sealed class Recorder
{
    static readonly string Bin = AppContext.BaseDirectory;

    readonly object gate = new();
    readonly KillOnCloseJob job = new();
    readonly List<Process> helpers = new();
    Process? ffmpeg;
    ClipRing? ring;
    static readonly string DiskRoot = Path.Combine(Path.GetTempPath(), "ReplayBuffer");
    List<string> trackTitles = new();
    int session;

    public event Action<string>? Log;

    public Recorder()
    {
        // remove disk buffers left behind by instances that are no longer running (crash, older versions)
        if (Directory.Exists(DiskRoot))
            foreach (string d in Directory.GetDirectories(DiskRoot))
                if (!int.TryParse(Path.GetFileName(d).Split('_')[0], out int pid) || !IsAlive(pid))
                    TryDelete(d);
    }

    public bool Running
    {
        get
        {
            lock (gate)
                return ffmpeg is { HasExited: false } && helpers.Count > 0 && helpers.All(p => !p.HasExited);
        }
    }

    // ---------------- device / monitor lists ----------------

    public static List<AudioDevice> ListAudioDevices()
    {
        var list = new List<AudioDevice>();
        foreach (string line in RunForOutput("audio.exe", "--list"))
        {
            string[] f = line.Split('\t');
            if (f.Length == 3) list.Add(new AudioDevice(f[1], f[2], f[0] == "out"));
        }
        return list;
    }

    public static List<MonitorInfo> ListMonitors()
    {
        var list = new List<MonitorInfo>();
        foreach (string line in RunForOutput("capture.exe", "--list"))
        {
            string[] f = line.Split('\t');
            if (f.Length == 4 && int.TryParse(f[0], out int i) && int.TryParse(f[1], out int w) && int.TryParse(f[2], out int h))
                list.Add(new MonitorInfo(i, w, h, f[3]));
        }
        return list;
    }

    static IEnumerable<string> RunForOutput(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Bin, exe), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            string all = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return all.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    // ---------------- start / stop ----------------

    public void Start(Settings s)
    {
        lock (gate)
        {
            StopLocked();
            string id = $"replaybuffer_{Environment.ProcessId}_{++session}";

            // shared epoch: video frame 0 and audio sample 0 both belong to this moment
            long epoch = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            int kbps = s.BitrateMbps * 1000;

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
            string videoPipe = $@"\\.\pipe\{id}_video";
            var capArgs = new List<string> { "--pipe", videoPipe, "--monitor", s.Monitor.ToString(), "--height", s.Height.ToString(),
                    "--fps", s.Fps.ToString(), "--kbps", kbps.ToString(), "--start", epoch.ToString() };
            capArgs.AddRange(new[] { "--rc", s.RateControl.ToString().ToLowerInvariant(), "--maxkbps", (s.MaxBitrateMbps * 1000).ToString(),
                    "--qp", s.Cqp.ToString(), "--quality", s.Preset switch { EncoderPreset.Speed => "0", EncoderPreset.Quality => "100", _ => "50" } });
            if (s.GameWindowOnly && s.Games.Count > 0) capArgs.AddRange(new[] { "--window", string.Join(";", s.Games) });
            if (Launch("capture.exe", capArgs.ToArray()) == null)
            {
                StopLocked();
                throw new InvalidOperationException("Screen capture could not start (see log).");
            }
            args.AddRange(new[] { "-thread_queue_size", "512", "-f", "ivf", "-probesize", "32", "-analyzeduration", "0", "-i", videoPipe });

            List<AudioDevice> devices = s.Tracks.Any(t => t.Kind == TrackKind.Device) ? ListAudioDevices() : new();
            trackTitles = new List<string>();
            int n = 0;
            foreach (AudioTrack t in s.Tracks)
            {
                var a = new List<string> { "--pipe", $@"\\.\pipe\{id}_audio{n}", "--epoch", epoch.ToString() };
                if (t.Kind == TrackKind.Device)
                {
                    AudioDevice? d = devices.FirstOrDefault(x => x.Id == t.DeviceId)
                                     ?? devices.FirstOrDefault(x => x.Name == t.DeviceName && x.IsOutput == t.Loopback);
                    if (d == null)
                    {
                        Log?.Invoke($"Audio device not found, track skipped: {t.DeviceName}");
                        continue;
                    }
                    a.AddRange(new[] { "--device", d.Id });
                    if (t.Loopback) a.Add("--loopback");
                }
                else
                {
                    var exes = t.Kind == TrackKind.Game ? s.Games : new List<string> { t.Exe };
                    if (exes.Count == 0)
                    {
                        Log?.Invoke("Game audio track skipped: no games in the list yet");
                        continue;
                    }
                    a.AddRange(new[] { "--apps", string.Join(";", exes) });
                }
                if (Launch("audio.exe", a) == null)
                {
                    Log?.Invoke($"Audio track failed to start: {t.Title}");
                    continue;
                }
                args.AddRange(new[] { "-f", "s16le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024",
                                      "-probesize", "32", "-analyzeduration", "0", "-i", a[1] });
                trackTitles.Add(t.Title);
                n++;
            }

            args.AddRange(new[] { "-map", "0:v", "-c:v", "copy" });
            for (int i = 0; i < trackTitles.Count; i++)
                args.AddRange(new[] { "-map", $"{i + 1}:a", $"-c:a:{i}", "aac", $"-b:a:{i}", "160k" });
            args.AddRange(new[] { "-f", "mpegts", "-mpegts_start_pid", "0x100", "-mpegts_pmt_start_pid", "0x1000",
                                  "-flush_packets", "1", "pipe:1" });
            ffmpeg = Start("ffmpeg.exe", args, "ffmpeg: ", stdout: true);
            ring = new ClipRing(s.BufferSeconds, s.BufferOnDisk ? Path.Combine(DiskRoot, $"{Environment.ProcessId}_{session}") : null);
            var (src, dst) = (ffmpeg.StandardOutput.BaseStream, ring);
            new Thread(() => dst.Fill(src)) { IsBackground = true, Name = "buffer" }.Start();
            string rate = s.RateControl switch
            {
                RateControl.CQP => $"CQP {s.Cqp}",
                RateControl.VBR => $"VBR {s.BitrateMbps}-{Math.Max(s.BitrateMbps, s.MaxBitrateMbps)} Mbps",
                _ => $"{s.BitrateMbps} Mbps",
            };
            Log?.Invoke($"Buffer started: {s.Height}p {(s.Fps == 0 ? "game frame rate" : s.Fps + " fps")}, {rate}, {trackTitles.Count} audio track(s), " +
                        $"{s.BufferSeconds / 60.0:0.#} min in {(s.BufferOnDisk ? "temp files on disk" : "memory")}");
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            bool was = ffmpeg != null;
            StopLocked();
            if (was) Log?.Invoke("Buffer stopped");
        }
    }

    void StopLocked()
    {
        foreach (Process p in helpers.Append(ffmpeg).OfType<Process>())
        {
            try { if (!p.HasExited) p.Kill(); p.WaitForExit(3000); } catch (Exception) { }
            p.Dispose();
        }
        helpers.Clear();
        ffmpeg = null;
        ring = null;
        // this instance's disk mode chunk files (any still being saved stay readable until closed)
        if (Directory.Exists(DiskRoot))
            foreach (string d in Directory.GetDirectories(DiskRoot, $"{Environment.ProcessId}_*"))
                TryDelete(d);
    }

    /// <summary>Starts a helper that serves a named pipe and waits for its "ready" line.</summary>
    Process? Launch(string exe, IEnumerable<string> args)
    {
        Process p = Start(exe, args, "", stdout: true);
        helpers.Add(p);
        var ready = Task.Run(() => p.StandardOutput.ReadLine());
        if (ready.Wait(5000) && ready.Result == "ready") return p;
        Log?.Invoke($"{exe} did not start");
        helpers.Remove(p);
        try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
        return null;
    }

    Process Start(string exe, IEnumerable<string> args, string logPrefix, bool stdout = false)
    {
        var psi = new ProcessStartInfo(Path.Combine(Bin, exe))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = stdout,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(logPrefix + e.Data); };
        p.Start();
        job.Add(p);
        p.BeginErrorReadLine();
        return p;
    }

    // ---------------- saving ----------------

    /// <summary>Takes the last N seconds from RAM (up to this moment) and muxes them into an MP4.</summary>
    public string? SaveClip(Settings s, string folder)
    {
        List<string> titles;
        ClipRing? r;
        lock (gate)
        {
            titles = trackTitles.ToList();
            r = ring;
        }
        if (r == null || !Running) return null;
        List<Stream> parts = r.Snapshot(s.BufferSeconds);
        if (parts.Count <= 1)
        {
            parts.ForEach(p => p.Dispose());
            return null;
        }

        string dir = Path.Combine(s.OutputDir, folder);
        Directory.CreateDirectory(dir);
        string outPath = Path.Combine(dir, $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "mpegts", "-i", "pipe:0" };
        if (titles.Count > 1)
        {
            // first track mixes everything, so ordinary players (which only play track 1) hear it all;
            // the separate tracks follow for editing
            string ins = string.Concat(Enumerable.Range(0, titles.Count).Select(i => $"[0:a:{i}]"));
            args.AddRange(new[] { "-filter_complex", $"{ins}amix=inputs={titles.Count}:normalize=0:dropout_transition=0,alimiter=limit=0.97[mix]",
                                  "-map", "0:v", "-map", "[mix]", "-map", "0:a", "-c", "copy", "-c:a:0", "aac", "-b:a:0", "192k",
                                  "-disposition:a", "0", "-disposition:a:0", "default" });  // mp4 muxer adds aac_adtstoasc itself
            titles.Insert(0, "All audio");
        }
        else args.AddRange(new[] { "-map", "0", "-c", "copy", "-bsf:a", "aac_adtstoasc" });
        args.AddRange(new[] { "-avoid_negative_ts", "make_zero", "-movflags", "+faststart" });
        for (int i = 0; i < titles.Count; i++)
            args.AddRange(new[] { $"-metadata:s:a:{i}", $"title={titles[i]}", $"-metadata:s:a:{i}", $"handler_name={titles[i]}" });
        args.Add(outPath);

        var psi = new ProcessStartInfo(Path.Combine(Bin, "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        try
        {
            using Stream stdin = p.StandardInput.BaseStream;
            foreach (Stream part in parts) part.CopyTo(stdin, 1 << 20);
        }
        catch (IOException) { }  // ffmpeg quit early; its error is reported below
        finally
        {
            parts.ForEach(x => x.Dispose());
        }
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Log?.Invoke("Save failed: " + err.Result.Trim());
            try { File.Delete(outPath); } catch (Exception) { }
            return null;
        }
        Log?.Invoke("Saved " + outPath);
        return outPath;
    }

    // ---------------- helpers ----------------

    static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception) { return false; }
    }

    /// <summary>Folder-friendly game name: "WardogsClient-Win64-Shipping.exe" -> "Wardogs".</summary>
    public static string ShortName(string exe)
    {
        string n = Path.GetFileNameWithoutExtension(exe);
        int cut = n.IndexOfAny(new[] { '-', '_', ' ', '.' });  // drops "-Win64-Shipping", "_x64", "_DX12"...
        if (cut > 0) n = n[..cut];
        foreach (string suffix in new[] { "Client", "Game", "Launcher", "Win64", "x64" })
            if (n.Length > suffix.Length + 1 && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                n = n[..^suffix.Length];
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c.ToString(), "");
        return n.Length > 0 ? n : "Game";
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
    }
}
