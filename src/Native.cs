using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ReplayBuffer;

/// <summary>Job object that kills every helper process if the app exits or crashes.</summary>
sealed class KillOnCloseJob
{
    readonly IntPtr handle = CreateJobObject(IntPtr.Zero, null);

    public KillOnCloseJob()
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000;  // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf(info);
        IntPtr p = Marshal.AllocHGlobal(size);
        Marshal.StructureToPtr(info, p, false);
        SetInformationJobObject(handle, 9 /* ExtendedLimitInformation */, p, (uint)size);
        Marshal.FreeHGlobal(p);
    }

    public void Add(Process p)
    {
        try { AssignProcessToJobObject(handle, p.Handle); } catch (Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

/// <summary>System-wide hotkey via RegisterHotKey (no keyboard hook, nothing touches the game).</summary>
sealed class GlobalHotkey : IDisposable
{
    const int Id = 0xB00F, WM_HOTKEY = 0x0312;
    const uint MOD_NOREPEAT = 0x4000;
    readonly HwndSource window;
    bool registered;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        window = new HwndSource(new HwndSourceParameters("ReplayBufferHotkey") { ParentWindow = new IntPtr(-3) });  // message-only
        window.AddHook(WndProc);
    }

    public bool Register(uint modifiers, uint vk)
    {
        Unregister();
        registered = RegisterHotKey(window.Handle, Id, modifiers | MOD_NOREPEAT, vk);
        return registered;
    }

    public void Unregister()
    {
        if (registered) UnregisterHotKey(window.Handle, Id);
        registered = false;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY && w.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        window.Dispose();
    }

    [DllImport("user32")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
