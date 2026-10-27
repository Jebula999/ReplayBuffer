using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayBuffer;

public enum TrackKind { Game, App, Device }

/// <summary>One audio track in the saved clip.</summary>
public sealed class AudioTrack
{
    public TrackKind Kind { get; set; }
    public string Exe { get; set; } = "";         // App
    public string DeviceId { get; set; } = "";    // Device
    public string DeviceName { get; set; } = "";  // Device (fallback match if the id changes)
    public bool Loopback { get; set; }            // Device: true = output ("what you hear"), false = input

    [JsonIgnore]
    public string Label => Kind switch
    {
        TrackKind.Game => "Game audio  (follows whichever listed game is running)",
        TrackKind.App => $"App:  {Exe}",
        _ => (Loopback ? "Output:  " : "Input:  ") + DeviceName,
    };

    // list display
    [JsonIgnore]
    public string Glyph => Kind switch { TrackKind.Game => "", TrackKind.App => "", _ => Loopback ? "" : "" };
    [JsonIgnore]
    public string Name => Kind == TrackKind.Game ? "Game audio" : Title;
    [JsonIgnore]
    public string Detail => Kind switch
    {
        TrackKind.Game => "Follows whichever listed game is running",
        TrackKind.App => "App · " + Exe,
        _ => Loopback ? "Output device · what you hear" : "Input device · microphone",
    };

    [JsonIgnore]
    public string Title => Kind switch
    {
        TrackKind.Game => "Game",
        TrackKind.App => Path.GetFileNameWithoutExtension(Exe),
        _ => DeviceName,
    };
}

public sealed class Settings
{
    public int BufferSeconds { get; set; } = 120;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 15;
    public int Monitor { get; set; } = 0;
    public bool GameWindowOnly { get; set; } = true;  // record just the game's window while it runs
    public bool BufferOnDisk { get; set; } = false;   // false = RAM
    public uint HotkeyModifiers { get; set; } = 0x1;  // Alt
    public uint HotkeyVk { get; set; } = 0x79;        // F10
    public string HotkeyText { get; set; } = "Alt + F10";
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Replays");
    public bool SaveSound { get; set; } = true;
    public bool AlwaysOn { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public List<string> Games { get; set; } = new();
    public List<AudioTrack> Tracks { get; set; } = new() { new AudioTrack { Kind = TrackKind.Game } };

    /// <summary>Portable copy: a "portable.txt" next to the exe keeps settings in .\Data instead of %APPDATA%.</summary>
    public static bool Portable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    public static string Folder { get; } = Portable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReplayBuffer");
    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception)
        {
            // corrupt file: fall back to defaults
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }
}
