using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ReplayBuffer;

public partial class App : Application
{
    public Settings Settings { get; private set; } = new();
    public Recorder Recorder { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public event Action? StateChanged;
    public string? HotkeyError { get; private set; }

    Mutex? singleInstance;
    GlobalHotkey? hotkey;
    Forms.NotifyIcon? tray;
    Forms.ToolStripMenuItem? trayToggle;
    Drawing.Icon? iconIdle, iconRec;
    MainWindow? window;
    readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer restartDebounce = new() { Interval = TimeSpan.FromMilliseconds(800) };
    readonly SemaphoreSlim busy = new(1, 1);
    bool? manual;          // user override until the game state next changes
    bool gameRunning;
    string? currentGame;   // exe of the listed game that's running, if any
    DateTime retryAfter;   // back-off after a failed start
    int saving;
    bool exiting;
    string logFile = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        singleInstance = new Mutex(true, "ReplayBuffer.SingleInstance", out bool first);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "ReplayBuffer.Show");
        var saveEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "ReplayBuffer.Save");
        if (!first)
        {
            // already running: "--save" saves a clip there, otherwise bring that window up
            (e.Args.Contains("--save") ? saveEvent : showEvent).Set();
            Shutdown();
            return;
        }
        new Thread(() => { while (showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowWindow); }) { IsBackground = true }.Start();
        new Thread(() => { while (saveEvent.WaitOne()) Dispatcher.BeginInvoke(SaveClip); }) { IsBackground = true }.Start();
        // the UI never uses the GPU, so it can't compete with a game (and avoids multi-GPU presentation issues)
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        Directory.CreateDirectory(Settings.Folder);
        logFile = Path.Combine(Settings.Folder, "log.txt");
        try { if (File.Exists(logFile) && new FileInfo(logFile).Length > 2_000_000) File.Delete(logFile); } catch (Exception) { }

        Settings = Settings.Load();
        Recorder.Log += AddLog;
        DispatcherUnhandledException += (_, ex) => { AddLog("Error: " + ex.Exception.Message); ex.Handled = true; };

        foreach (string f in new[] { "capture.exe", "audio.exe", "ffmpeg.exe" })
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, f)))
            {
                MessageBox.Show($"{f} is missing from the app folder. Reinstall Replay Buffer.", "Replay Buffer",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

        hotkey = new GlobalHotkey();
        hotkey.Pressed += SaveClip;
        ApplyHotkey();
        SetupTray();
        ApplyStartWithWindows();

        watch.Tick += (_, _) => Tick();
        watch.Start();
        restartDebounce.Tick += (_, _) => { restartDebounce.Stop(); _ = Restart(); };

        AddLog("Replay Buffer started");
        if (!e.Args.Contains("--minimized")) ShowWindow();
        int snap = Array.IndexOf(e.Args, "--snapshot");  // diagnostics: render the window to a PNG
        if (snap >= 0 && snap + 1 < e.Args.Length)
        {
            string file = e.Args[snap + 1];
            // off-screen and unfocused, so it never pops up over a game
            window ??= new MainWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0 };
            double h = window.Height;
            window.Height = 2400;  // capture the whole page
            window.Show();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                window.Snapshot(file);
                window.Hide();
                window.Height = h;
                window.Left = (SystemParameters.WorkArea.Width - window.Width) / 2;
                window.Top = Math.Max(0, (SystemParameters.WorkArea.Height - window.Height) / 2);
            };
            t.Start();
        }
        Tick();
    }

    // ---------------- window / tray ----------------

    public void ShowWindow()
    {
        window ??= new MainWindow();
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public bool Exiting => exiting;

    void SetupTray()
    {
        iconIdle = MakeIcon(Drawing.Color.FromArgb(140, 140, 140));
        iconRec = MakeIcon(Drawing.Color.FromArgb(232, 17, 35));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Replay Buffer", null, (_, _) => ShowWindow());
        menu.Items.Add("Save clip now", null, (_, _) => SaveClip());
        trayToggle = new Forms.ToolStripMenuItem("Start buffer", null, (_, _) => ToggleBuffer());
        menu.Items.Add(trayToggle);
        menu.Items.Add("Open clips folder", null, (_, _) => OpenClipsFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        tray = new Forms.NotifyIcon { Icon = iconIdle, Text = "Replay Buffer", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
    }

    static Drawing.Icon MakeIcon(Drawing.Color c)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var ring = new Drawing.Pen(c, 3);
            g.DrawEllipse(ring, 2.5f, 2.5f, 27, 27);
            using var dot = new Drawing.SolidBrush(c);
            g.FillEllipse(dot, 9, 9, 14, 14);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void ExitApp()
    {
        exiting = true;
        watch.Stop();
        Recorder.Stop();
        hotkey?.Dispose();
        if (tray != null) tray.Visible = false;
        window?.Close();
        Shutdown();
    }

    public void OpenClipsFolder()
    {
        Directory.CreateDirectory(Settings.OutputDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.OutputDir}\"") { UseShellExecute = true });
    }

    // ---------------- buffer control ----------------

    public bool Running => Recorder.Running;

    public void ToggleBuffer()
    {
        manual = !Recorder.Running;
        _ = SetRunning(manual.Value);
    }

    void Tick()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process p in Process.GetProcesses())
        {
            running.Add(p.ProcessName + ".exe");
            p.Dispose();
        }
        string? game = Settings.Games.FirstOrDefault(running.Contains);
        currentGame = game;
        if ((game != null) != gameRunning)
        {
            gameRunning = game != null;
            manual = null;
            if (!Settings.AlwaysOn) AddLog(gameRunning ? $"Game detected: {game}" : "Game closed");
        }
        bool want = manual ?? (Settings.AlwaysOn || gameRunning);
        if (want != Recorder.Running && DateTime.Now >= retryAfter)
        {
            if (want && !Recorder.Running && busy.CurrentCount > 0 && !manual.HasValue && wasRunning)
                AddLog("Buffer stopped unexpectedly, restarting");
            _ = SetRunning(want);
        }
        UpdateState();
    }

    bool wasRunning;

    async Task SetRunning(bool on)
    {
        if (!await busy.WaitAsync(0)) return;
        try
        {
            await Task.Run(() => { if (on) Recorder.Start(Settings); else Recorder.Stop(); });
            if (on) await Task.Delay(1500);  // let the pipeline settle before judging it
            if (on && !Recorder.Running)
            {
                retryAfter = DateTime.Now.AddSeconds(15);
                Recorder.Stop();
                AddLog("Buffer could not start; retrying in 15 s");
            }
        }
        catch (Exception ex)
        {
            retryAfter = DateTime.Now.AddSeconds(15);
            AddLog("Buffer could not start: " + ex.Message);
        }
        finally
        {
            busy.Release();
            UpdateState();
        }
    }

    async Task Restart()
    {
        if (!Recorder.Running) return;
        if (!await busy.WaitAsync(0)) { restartDebounce.Start(); return; }
        busy.Release();
        await SetRunning(true);  // Start() tears down the old pipeline first
    }

    /// <summary>Called when a setting that affects capture changes.</summary>
    public void SettingsChanged(bool restartCapture)
    {
        Settings.Save();
        if (restartCapture && Recorder.Running)
        {
            restartDebounce.Stop();
            restartDebounce.Start();
        }
        UpdateState();
    }

    void UpdateState()
    {
        bool r = Recorder.Running;
        wasRunning = r;
        if (tray != null)
        {
            tray.Icon = r ? iconRec : iconIdle;
            tray.Text = r ? "Replay Buffer - buffering" : "Replay Buffer - idle";
        }
        if (trayToggle != null) trayToggle.Text = r ? "Stop buffer" : "Start buffer";
        StateChanged?.Invoke();
    }

    // ---------------- saving ----------------

    public void SaveClip()
    {
        if (Interlocked.Exchange(ref saving, 1) == 1) return;
        if (!Recorder.Running)
        {
            AddLog("Nothing to save: the buffer isn't running");
            Sounds.Play(false, Settings.SaveSound);
            saving = 0;
            return;
        }
        string folder = currentGame != null ? Recorder.ShortName(currentGame) : "Desktop";
        Sounds.Play(true, Settings.SaveSound);  // instant feedback; the clip itself is already safe in RAM
        Task.Run(() => Recorder.SaveClip(Settings, folder)).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result == null) Sounds.Play(false, Settings.SaveSound);
            if (t.Exception != null) AddLog("Save failed: " + t.Exception.GetBaseException().Message);
            saving = 0;
        });
    }

    // ---------------- hotkey / startup ----------------

    public void SuspendHotkey() => hotkey?.Unregister();

    public void ApplyHotkey()
    {
        if (hotkey == null) return;
        HotkeyError = hotkey.Register(Settings.HotkeyModifiers, Settings.HotkeyVk)
            ? null
            : $"{Settings.HotkeyText} is already used by another program. Pick a different shortcut.";
        if (HotkeyError != null) AddLog(HotkeyError);
        StateChanged?.Invoke();
    }

    public void ApplyStartWithWindows()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run == null) return;
            if (Settings.StartWithWindows)
                run.SetValue("ReplayBuffer", $"\"{Environment.ProcessPath}\" --minimized");
            else if (run.GetValue("ReplayBuffer") != null)
                run.DeleteValue("ReplayBuffer");
        }
        catch (Exception ex)
        {
            AddLog("Could not change startup setting: " + ex.Message);
        }
    }

    // ---------------- log ----------------

    public void AddLog(string text)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {text}";
        try { File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}"); } catch (Exception) { }
        Dispatcher.BeginInvoke(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
        });
    }
}
