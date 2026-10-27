// Replay Buffer setup: per-user install (no admin) of the embedded payload.zip.
//   Setup.exe              install / update (shows a small dialog)
//   Setup.exe /S           silent install to the default folder
//   Uninstall.exe /uninstall   (copied into the install folder; registered in Apps & features)
// Built with the .NET Framework compiler that ships with Windows (see package.ps1), so C# 5 only.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Replay Buffer Setup")]
[assembly: AssemblyProduct("Replay Buffer")]
[assembly: AssemblyVersion(Setup.Version + ".0")]
[assembly: AssemblyFileVersion(Setup.Version + ".0")]

static class Setup
{
    public const string Version = "1.0.0";
    const string Name = "Replay Buffer";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ReplayBuffer";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string DefaultDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ReplayBuffer"); }
    }
    static string StartMenuLink
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); }
    }
    static string DesktopLink
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (Has(args, "/uninstall")) return Uninstall(Has(args, "/S"));
            if (Has(args, "/S")) { Install(DefaultDir, false, false); return 0; }
            Application.Run(new SetupForm());
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static bool Has(string[] args, string flag)
    {
        foreach (string a in args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void CloseRunningApp()
    {
        foreach (Process p in Process.GetProcessesByName("ReplayBuffer"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }  // its helpers exit with it
        }
        Thread.Sleep(500);
    }

    public static void Install(string dir, bool desktop, bool launch)
    {
        CloseRunningApp();
        Directory.CreateDirectory(dir);
        long bytes = 0;
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                if (e.Name.Length == 0) continue;
                string dst = Path.Combine(dir, e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                e.ExtractToFile(dst, true);
                bytes += e.Length;
            }
        }
        string self = Assembly.GetExecutingAssembly().Location;
        string uninst = Path.Combine(dir, "Uninstall.exe");
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, uninst, true);

        string exe = Path.Combine(dir, "ReplayBuffer.exe");
        Shortcut(StartMenuLink, exe, dir);
        if (desktop) Shortcut(DesktopLink, exe, dir);

        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", Name);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", Name);
            k.SetValue("DisplayIcon", exe);
            k.SetValue("InstallLocation", dir);
            k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /S");
            k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        if (launch) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir });
    }

    static int Uninstall(bool silent)
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!File.Exists(Path.Combine(dir, "ReplayBuffer.exe")))
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                if (k != null && k.GetValue("InstallLocation") is string) dir = (string)k.GetValue("InstallLocation");
        }
        if (!silent && MessageBox.Show("Remove Replay Buffer from this PC?\n\nYour saved clips are not touched.", Name,
                                       MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1;
        bool wipeSettings = !silent && MessageBox.Show("Also delete your Replay Buffer settings?", Name,
                                                       MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        CloseRunningApp();
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (run != null && run.GetValue("ReplayBuffer") != null) run.DeleteValue("ReplayBuffer");
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (wipeSettings)
        {
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReplayBuffer"), true); }
            catch (Exception) { }
        }
        foreach (string f in new[] { "ReplayBuffer.exe", "capture.exe", "audio.exe", "ffmpeg.exe", "THIRD-PARTY-NOTICES.txt", "ffmpeg-LICENSE.txt" })
            TryDelete(Path.Combine(dir, f));
        // this exe is still running: remove it and the folder once it exits
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"")
        {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
        });
        if (!silent) MessageBox.Show("Replay Buffer was removed.", Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
    }

    static void Shortcut(string link, string target, string workDir)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
        Type lt = lnk.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
        lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
        lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
        lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Replay Buffer" });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }
}

sealed class SetupForm : Form
{
    readonly TextBox path = new TextBox();
    readonly CheckBox desktop = new CheckBox();
    readonly CheckBox launch = new CheckBox();
    readonly Button install = new Button();

    public SetupForm()
    {
        Text = "Replay Buffer Setup";
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 270);
        BackColor = Color.White;
        try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch (Exception) { }

        var title = new Label { Text = "Install Replay Buffer " + Setup.Version, Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(24, 20) };
        var sub = new Label
        {
            Text = "Keeps the last few minutes of your game in memory and saves a clip when you press a key.",
            AutoSize = false, Size = new Size(470, 40), Location = new Point(26, 58), ForeColor = Color.FromArgb(90, 90, 90),
        };
        var where = new Label { Text = "Install folder", AutoSize = true, Location = new Point(26, 104) };
        path.Text = ExistingDir() ?? DefaultDirPublic();
        path.Location = new Point(26, 126);
        path.Size = new Size(380, 26);
        var browse = new Button { Text = "Browse...", Location = new Point(414, 124), Size = new Size(82, 29) };
        browse.Click += delegate
        {
            using (var d = new FolderBrowserDialog { Description = "Install Replay Buffer to:", SelectedPath = path.Text })
                if (d.ShowDialog(this) == DialogResult.OK) path.Text = Path.Combine(d.SelectedPath, "ReplayBuffer");
        };
        desktop.Text = "Create a desktop shortcut";
        desktop.AutoSize = true;
        desktop.Location = new Point(26, 166);
        launch.Text = "Start Replay Buffer when setup finishes";
        launch.AutoSize = true;
        launch.Checked = true;
        launch.Location = new Point(26, 192);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(243, 243, 243) };
        install.Text = ExistingDir() != null ? "Update" : "Install";
        install.Size = new Size(100, 32);
        install.Location = new Point(300, 12);
        install.Click += delegate { DoInstall(); };
        var cancel = new Button { Text = "Cancel", Size = new Size(100, 32), Location = new Point(408, 12), DialogResult = DialogResult.Cancel };
        footer.Controls.Add(install);
        footer.Controls.Add(cancel);
        AcceptButton = install;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { title, sub, where, path, browse, desktop, launch, footer });
    }

    static string DefaultDirPublic()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ReplayBuffer");
    }

    static string ExistingDir()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ReplayBuffer"))
            return k != null ? k.GetValue("InstallLocation") as string : null;
    }

    void DoInstall()
    {
        install.Enabled = false;
        Cursor = Cursors.WaitCursor;
        try
        {
            Setup.Install(path.Text.Trim(), desktop.Checked, launch.Checked);
            Cursor = Cursors.Default;
            MessageBox.Show(this, "Replay Buffer is installed. Find it in the Start menu.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            Cursor = Cursors.Default;
            install.Enabled = true;
            MessageBox.Show(this, "Setup failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
