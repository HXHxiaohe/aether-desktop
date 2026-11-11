using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Aether.Core;

public enum AutostartMode { Off, High, Normal, Low }

/// <summary>
/// Start with Windows, three levels (no admin rights needed for any of them):
///   High   – current-user logon task, starts right away (doesn't depend on Explorer's Run pass,
///            which some "optimizer" tools delay or skip)
///   Normal – the usual HKCU Run entry (shows in Task Manager › 启动应用)
///   Low    – logon task delayed 60 s, so Aether doesn't compete with the rest of start-up
/// Only one mechanism is active at a time.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "AetherDesktop";
    public const string TaskName = "Aether Desktop";

    private static string Exe => Environment.ProcessPath ?? "";
    private static string Command => $"\"{Exe}\" --autostart";

    private static string? RunValue
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) as string;
        }
    }

    /// <summary>Current level and where it points.</summary>
    public static (AutostartMode Mode, string? Target) Read()
    {
        var xml = Schtasks($"/Query /TN \"{TaskName}\" /XML", out int code);
        if (code == 0 && xml.Contains("<Task", StringComparison.Ordinal))
        {
            var cmd = Regex.Match(xml, "<Command>(.*?)</Command>").Groups[1].Value;
            var delay = Regex.Match(xml, "<Delay>(.*?)</Delay>").Groups[1].Value;
            bool enabled = !Regex.IsMatch(xml, @"<Settings>[\s\S]*?<Enabled>false</Enabled>");
            if (enabled)
                return (delay is "" or "PT0S" ? AutostartMode.High : AutostartMode.Low, SecurityElementUnescape(cmd));
        }
        var run = RunValue;
        if (!string.IsNullOrWhiteSpace(run)) return (AutostartMode.Normal, run.Split('"', StringSplitOptions.RemoveEmptyEntries)[0]);
        return (AutostartMode.Off, null);
    }

    public static bool PointsHere(string? target) =>
        target is not null && string.Equals(Path.GetFullPath(target.Trim()), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Switch level. Returns null on success, or a short reason it failed.</summary>
    public static string? Set(AutostartMode mode)
    {
        try
        {
            if (mode is AutostartMode.High or AutostartMode.Low)
            {
                var err = RegisterTask(mode == AutostartMode.Low ? "PT60S" : "PT0S");
                if (err is not null) return err;
                SetRun(false);
            }
            else
            {
                SetRun(mode == AutostartMode.Normal);
                DeleteTask();
            }
            return null;
        }
        catch (Exception ex) { Log.Error("autostart", ex); return ex.Message; }
    }

    private static void SetRun(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, Command);
        else if (k.GetValue(Name) is not null) k.DeleteValue(Name);
    }

    private static void DeleteTask()
    {
        Schtasks($"/Query /TN \"{TaskName}\"", out int code);
        if (code == 0) Schtasks($"/Delete /TN \"{TaskName}\" /F", out _);
    }

    private static string? RegisterTask(string delay)
    {
        string sid;
        using (var id = WindowsIdentity.GetCurrent()) sid = id.User!.Value;
        string E(string s) => SecurityElement.Escape(s) ?? "";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Aether Desktop · 登录后启动</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{E(sid)}</UserId><Delay>{delay}</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="User"><UserId>{E(sid)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>6</Priority>
              </Settings>
              <Actions Context="User"><Exec><Command>{E(Exe)}</Command><Arguments>--autostart</Arguments><WorkingDirectory>{E(Path.GetDirectoryName(Exe) ?? "")}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "aether-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            var output = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out int code);
            return code == 0 ? null : output.Trim();
        }
        finally { try { File.Delete(file); } catch { } }
    }

    private static string SecurityElementUnescape(string s) =>
        s.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    private static string Schtasks(string args, out int exitCode)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        exitCode = p.HasExited ? p.ExitCode : -1;
        return exitCode == 0 ? stdout : stderr + stdout;
    }
}
