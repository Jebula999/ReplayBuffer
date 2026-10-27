using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ReplayBuffer;

public partial class MainWindow : Window
{
    static readonly (string Label, int Height)[] Resolutions = { ("720p", 720), ("1080p", 1080), ("1440p", 1440) };
    static readonly int[] FrameRates = { 30, 60, 144 };
    static readonly int[] Bitrates = { 5, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100 };
    static readonly (string Label, int Seconds)[] Buffers =
    {
        ("30 seconds", 30), ("1 minute", 60), ("2 minutes", 120), ("3 minutes", 180), ("5 minutes", 300),
        ("10 minutes", 600), ("15 minutes", 900), ("20 minutes", 1200), ("30 minutes", 1800),
    };

    readonly App app = (App)Application.Current;
    Settings S => app.Settings;
    bool loading = true;

    public MainWindow()
    {
        InitializeComponent();
        Background = SystemIsDark() ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)) : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        Load();
        app.StateChanged += () => Dispatcher.BeginInvoke(RefreshStatus);
        app.LogLines.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(RefreshLog);
        RefreshStatus();
        RefreshLog();
        loading = false;
    }

    public static bool SystemIsDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    public void Snapshot(string file)
    {
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!app.Exiting)
        {
            e.Cancel = true;  // keep running in the tray
            Hide();
        }
        base.OnClosing(e);
    }

    // ---------------- load ----------------

    void Load()
    {
        foreach (var r in Resolutions) ResolutionBox.Items.Add(r.Label);
        ResolutionBox.SelectedIndex = Math.Max(0, Array.FindIndex(Resolutions, r => r.Height == S.Height));
        foreach (int f in FrameRates) FpsBox.Items.Add($"{f} fps");
        FpsBox.SelectedIndex = Math.Max(0, Array.IndexOf(FrameRates, S.Fps));
        foreach (int b in Bitrates) BitrateBox.Items.Add(b == 15 ? "15 Mbps (default)" : $"{b} Mbps");
        BitrateBox.SelectedIndex = Math.Max(0, Array.IndexOf(Bitrates, S.BitrateMbps));
        foreach (var b in Buffers) BufferBox.Items.Add(b.Label);
        BufferBox.SelectedIndex = Math.Max(0, Array.FindIndex(Buffers, b => b.Seconds == S.BufferSeconds));
        StorageBox.SelectedIndex = S.BufferOnDisk ? 1 : 0;

        var monitors = Recorder.ListMonitors();
        if (monitors.Count == 0) monitors.Add(new MonitorInfo(0, 0, 0, "Main display"));
        foreach (var m in monitors)
            MonitorBox.Items.Add(m.Width > 0 ? $"Display {m.Index + 1}  ({m.Width} x {m.Height})" : m.Name);
        MonitorBox.SelectedIndex = Math.Clamp(S.Monitor, 0, monitors.Count - 1);
        WindowOnlyBox.IsChecked = S.GameWindowOnly;

        HotkeyBox.Text = S.HotkeyText;
        FolderBox.Text = S.OutputDir;
        SoundBox.IsChecked = S.SaveSound;
        StartupBox.IsChecked = S.StartWithWindows;
        (S.AlwaysOn ? ModeAlways : ModeGames).IsChecked = true;
        RefreshTracks();
        RefreshGames();
    }

    void RefreshTracks()
    {
        TrackList.ItemsSource = null;
        TrackList.ItemsSource = S.Tracks;
        RefreshEstimate();
    }

    // constant bitrate, so size depends only on bitrate, track count and length
    void RefreshEstimate()
    {
        int tracks = S.Tracks.Count;
        double audio = tracks * 160_000 + (tracks > 1 ? 192_000 : 0);                   // bits/s in the clip
        double clip = (S.BitrateMbps * 1e6 + audio) / 8 * S.BufferSeconds * 1.01;        // MP4 overhead
        double ram = (S.BitrateMbps * 1e6 + tracks * 160_000) / 8 * (S.BufferSeconds + 4) * 1.06;  // MPEG-TS overhead
        EstimateClip.Text = "≈ " + Size(clip) + " per clip";
        EstimateRam.Text = Size(ram) + (S.BufferOnDisk ? " of temp disk space" : " of memory") + " while buffering";
        string len = Buffers.FirstOrDefault(b => b.Seconds == S.BufferSeconds).Label ?? $"{S.BufferSeconds} s";
        EstimateDetail.Text = $"{len} at {S.BitrateMbps} Mbps with {tracks} audio track{(tracks == 1 ? "" : "s")}. Resolution and frame rate don't change it.";
    }

    static string Size(double bytes) => bytes >= 1e9 ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0} MB";

    void RefreshGames()
    {
        GameList.ItemsSource = S.Games.Select(g => new { Exe = g, Name = Recorder.ShortName(g) }).ToList();
    }

    void RefreshStatus()
    {
        bool r = app.Running;
        StatusDot.Fill = StatusHalo.Fill = r ? new SolidColorBrush(Color.FromRgb(232, 17, 35)) : Brushes.Gray;
        StatusText.Text = r ? "Buffering" : "Idle";
        string buf = Buffers.FirstOrDefault(b => b.Seconds == S.BufferSeconds).Label ?? $"{S.BufferSeconds} s";
        StatusSub.Text = r
            ? $"Keeping the last {buf.ToLowerInvariant()} {(S.BufferOnDisk ? "on disk" : "in memory")}. Press {S.HotkeyText} to save."
            : S.AlwaysOn ? "Starting..." : S.Games.Count == 0
                ? "Add a game below, or choose \"Always\"."
                : "Waiting for a game to start.";
        ToggleButton.Content = r ? "Stop buffer" : "Start buffer";
        SaveButton.IsEnabled = r;
        HotkeyHint.Text = app.HotkeyError ?? "Click the box, then press the keys you want.";
        HotkeyHint.Foreground = app.HotkeyError != null ? Brushes.IndianRed : TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
    }

    void RefreshLog()
    {
        LogBox.Text = string.Join(Environment.NewLine, app.LogLines);
        LogBox.ScrollToEnd();
    }

    // ---------------- header ----------------

    void Toggle_Click(object sender, RoutedEventArgs e) => app.ToggleBuffer();
    void Save_Click(object sender, RoutedEventArgs e) => app.SaveClip();

    // ---------------- video / replay ----------------

    void Video_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        S.Height = Resolutions[ResolutionBox.SelectedIndex].Height;
        S.Fps = FrameRates[FpsBox.SelectedIndex];
        S.BitrateMbps = Bitrates[BitrateBox.SelectedIndex];
        S.Monitor = MonitorBox.SelectedIndex;
        S.BufferSeconds = Buffers[BufferBox.SelectedIndex].Seconds;
        S.BufferOnDisk = StorageBox.SelectedIndex == 1;
        RefreshEstimate();
        app.SettingsChanged(restartCapture: true);
    }

    void WindowOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        S.GameWindowOnly = WindowOnlyBox.IsChecked == true;
        app.SettingsChanged(restartCapture: true);
    }

    void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        S.SaveSound = SoundBox.IsChecked == true;
        app.SettingsChanged(false);
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Where should clips be saved?", InitialDirectory = S.OutputDir };
        if (dlg.ShowDialog(this) != true) return;
        S.OutputDir = dlg.FolderName;
        FolderBox.Text = S.OutputDir;
        app.SettingsChanged(false);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => app.OpenClipsFolder();

    // ---------------- hotkey capture ----------------

    void Hotkey_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        app.SuspendHotkey();  // so the current shortcut can be pressed again
        HotkeyBox.Text = "Press a key combination...";
    }

    void Hotkey_Blur(object sender, KeyboardFocusChangedEventArgs e)
    {
        HotkeyBox.Text = S.HotkeyText;
        app.ApplyHotkey();
    }

    void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            Focus();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
            return;  // wait for the actual key

        ModifierKeys mods = Keyboard.Modifiers;
        uint m = 0;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) { m |= 0x2; parts.Add("Ctrl"); }
        if (mods.HasFlag(ModifierKeys.Alt)) { m |= 0x1; parts.Add("Alt"); }
        if (mods.HasFlag(ModifierKeys.Shift)) { m |= 0x4; parts.Add("Shift"); }
        if (mods.HasFlag(ModifierKeys.Windows)) { m |= 0x8; parts.Add("Win"); }
        parts.Add(KeyName(key));

        S.HotkeyModifiers = m;
        S.HotkeyVk = (uint)KeyInterop.VirtualKeyFromKey(key);
        S.HotkeyText = string.Join(" + ", parts);
        app.SettingsChanged(false);
        Keyboard.ClearFocus();  // triggers Hotkey_Blur, which registers the new shortcut
        Focus();
    }

    static string KeyName(Key k) => k switch
    {
        Key.Multiply => "Num *",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Divide => "Num /",
        Key.Decimal => "Num .",
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (k - Key.NumPad0),
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        Key.Prior => "Page Up",
        Key.Next => "Page Down",
        Key.Snapshot => "Print Screen",
        Key.Scroll => "Scroll Lock",
        Key.Back => "Backspace",
        Key.Return => "Enter",
        _ => k.ToString(),
    };

    // ---------------- audio tracks ----------------

    void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is AudioTrack t) S.Tracks.Remove(t);
        RefreshTracks();
        app.SettingsChanged(true);
    }

    void AddGameTrack_Click(object sender, RoutedEventArgs e)
    {
        if (S.Tracks.Any(t => t.Kind == TrackKind.Game)) return;
        S.Tracks.Insert(0, new AudioTrack { Kind = TrackKind.Game });
        RefreshTracks();
        app.SettingsChanged(true);
    }

    void AddAppTrack_Click(object sender, RoutedEventArgs e)
    {
        string? exe = PickRunningProgram("Record audio from which app?");
        if (exe == null || S.Tracks.Any(t => t.Kind == TrackKind.App && t.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))) return;
        S.Tracks.Add(new AudioTrack { Kind = TrackKind.App, Exe = exe });
        RefreshTracks();
        app.SettingsChanged(true);
    }

    void AddDeviceTrack_Click(object sender, RoutedEventArgs e)
    {
        var devices = Recorder.ListAudioDevices();
        var items = devices.OrderBy(d => !d.IsOutput).ThenBy(d => d.Name)
            .Select(d => new PickerItem((d.IsOutput ? "Output (what you hear):  " : "Input (microphone):  ") + d.Name, d)).ToList();
        var picker = new PickerWindow("Record which device?", items) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected?.Value is not AudioDevice d) return;
        if (S.Tracks.Any(t => t.Kind == TrackKind.Device && t.DeviceId == d.Id)) return;
        S.Tracks.Add(new AudioTrack { Kind = TrackKind.Device, DeviceId = d.Id, DeviceName = d.Name, Loopback = d.IsOutput });
        RefreshTracks();
        app.SettingsChanged(true);
    }

    // ---------------- games ----------------

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        S.AlwaysOn = ModeAlways.IsChecked == true;
        app.SettingsChanged(false);
    }

    void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string g) S.Games.Remove(g);
        RefreshGames();
        app.SettingsChanged(S.GameWindowOnly || S.Tracks.Any(t => t.Kind == TrackKind.Game));
    }

    void AddGame(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return;
        exe = Path.GetFileName(exe);
        if (S.Games.Any(g => g.Equals(exe, StringComparison.OrdinalIgnoreCase))) return;
        S.Games.Add(exe);
        RefreshGames();
        app.SettingsChanged(S.GameWindowOnly || S.Tracks.Any(t => t.Kind == TrackKind.Game));
    }

    void AddRunningGame_Click(object sender, RoutedEventArgs e) => AddGame(PickRunningProgram("Which program is the game?"));

    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose the game's .exe", Filter = "Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) == true) AddGame(dlg.FileName);
    }

    string? PickRunningProgram(string title)
    {
        // programs with a window first, then everything else in this session
        var withWindow = new List<PickerItem>();
        var others = new List<PickerItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int session = Process.GetCurrentProcess().SessionId;
        foreach (Process p in Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session || p.Id == Environment.ProcessId) continue;
                    string exe = p.ProcessName + ".exe";
                    if (!seen.Add(exe)) continue;
                    if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0)
                        withWindow.Add(new PickerItem($"{p.MainWindowTitle}   ({exe})", exe));
                    else
                        others.Add(new PickerItem(exe, exe));
                }
                catch (Exception) { }
            }
        }
        var picker = new PickerWindow(title, withWindow.Concat(others).ToList()) { Owner = this };
        return picker.ShowDialog() == true ? picker.Selected?.Value as string : null;
    }

    // ---------------- general ----------------

    void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        S.StartWithWindows = StartupBox.IsChecked == true;
        app.SettingsChanged(false);
        app.ApplyStartWithWindows();
    }
}
