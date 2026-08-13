using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Cuckoo.Core;
using Cuckoo.Models;

namespace Cuckoo.Services;

/// <summary>
/// Manages "start with Windows" via two mechanisms, both scoped per install folder
/// so multiple instances coexist:
///   - Registry: a per-instance HKCU\...\Run value ("Cuckoo_{instanceId}")
///   - TaskScheduler: a per-instance logon task ("Cuckoo_{instanceId}")
///
/// The previous version wrote a single shared "Cuckoo" Run value, so a second instance
/// enabling autostart overwrote the first. <see cref="MigrateLegacy"/> converts that old
/// shared value into the per-instance form for whichever instance it pointed at.
/// </summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacySharedValue = "Cuckoo";           // pre-fix shared Run value
    private const string LegacyOldNameValue = "TwitchDropsMiner"; // pre-rename Run value

    private static string RegistryValueName => $"Cuckoo_{Constants.InstanceId}";
    private static string TaskName => $"Cuckoo_{Constants.InstanceId}";

    private static string ExePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Unable to determine the executable path");

    private static string RegistryCommand(bool tray)
        => tray ? $"\"{ExePath}\" --tray" : $"\"{ExePath}\"";

    // ------------------------------------------------------------------ status

    /// <summary>The method currently enabling autostart for this instance, or null.</summary>
    public static AutostartMethod? CurrentMethod()
    {
        if (TaskExists())
            return AutostartMethod.TaskScheduler;
        if (RegistryEnabled())
            return AutostartMethod.Registry;
        return null;
    }

    public static bool IsEnabled() => CurrentMethod() is not null;

    /// <summary>Whether the active autostart entry launches minimized to tray.</summary>
    public static bool IsTray()
    {
        try
        {
            if (RegistryEnabled())
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RegistryValueName) is string cmd
                    && cmd.Contains("--tray", StringComparison.OrdinalIgnoreCase);
            }
            if (TaskExists())
                return QueryTaskCommand()?.Contains("--tray", StringComparison.OrdinalIgnoreCase) ?? false;
        }
        catch (Exception ex)
        {
            Logger.Instance.Exception("Autostart tray query", ex);
        }
        return false;
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Applies the desired autostart state. Sets up the chosen method and removes the other,
    /// so switching methods never leaves a stale entry behind.
    /// </summary>
    public static void Apply(bool enabled, AutostartMethod method, bool tray, int delaySeconds)
    {
        try
        {
            if (!enabled)
            {
                RemoveRegistry();
                RemoveTask();
                Logger.Instance.Info("Autostart disabled");
                return;
            }
            if (method == AutostartMethod.Registry)
            {
                RemoveTask();
                SetRegistry(tray);
            }
            else
            {
                RemoveRegistry();
                SetTask(tray, delaySeconds);
            }
            Logger.Instance.Info(
                $"Autostart enabled via {method}{(tray ? " (tray)" : "")}"
                + (method == AutostartMethod.TaskScheduler && delaySeconds > 0 ? $", {delaySeconds}s delay" : ""));
        }
        catch (Exception ex)
        {
            Logger.Instance.Exception("Autostart apply", ex);
        }
    }

    // ------------------------------------------------------------------ registry

    private static bool RegistryEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RegistryValueName) is not null;
        }
        catch (Exception ex)
        {
            Logger.Instance.Exception("Autostart registry query", ex);
            return false;
        }
    }

    private static void SetRegistry(bool tray)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RegistryValueName, RegistryCommand(tray));
    }

    private static void RemoveRegistry()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(RegistryValueName, throwOnMissingValue: false);
    }

    // ------------------------------------------------------------------ task scheduler

    private static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    private static string? QueryTaskCommand()
    {
        // /XML dumps the task definition; the <Command>/<Arguments> reveal the tray flag
        return RunSchtasks($"/Query /TN \"{TaskName}\" /XML", out string output) == 0 ? output : null;
    }

    private static void SetTask(bool tray, int delaySeconds)
    {
        string user = WindowsIdentity.GetCurrent().Name; // DOMAIN\User
        string xml = BuildTaskXml(user, tray, delaySeconds);
        string xmlPath = Path.Combine(Path.GetTempPath(), $"{TaskName}.xml");
        // Task Scheduler expects the XML file as Unicode
        File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try
        {
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out string output);
            if (code != 0)
                Logger.Instance.Warning($"schtasks create failed ({code}): {output.Trim()}");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (Exception) { }
        }
    }

    private static void RemoveTask()
    {
        if (TaskExists())
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out _);
    }

    private static string BuildTaskXml(string user, bool tray, int delaySeconds)
    {
        string exe = ExePath;
        string args = tray ? "--tray" : "";
        string delay = delaySeconds > 0 ? $"<Delay>PT{delaySeconds}S</Delay>" : "";
        string workingDir = Constants.WorkingDir.TrimEnd(Path.DirectorySeparatorChar);
        // InteractiveToken + LeastPrivilege: runs in the user's interactive session
        // (GUI visible) without needing a stored password, at normal privileges.
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts Cuckoo at logon. Instance: {Xml(workingDir)}</Description>
            <Author>Cuckoo</Author>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Xml(user)}</UserId>
              {delay}
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Xml(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Xml(exe)}</Command>
              <Arguments>{Xml(args)}</Arguments>
              <WorkingDirectory>{Xml(workingDir)}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    private static int RunSchtasks(string arguments, out string output)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception ex)
        {
            Logger.Instance.Exception("schtasks invocation", ex);
            output = ex.Message;
            return -1;
        }
    }

    // ------------------------------------------------------------------ migration

    /// <summary>
    /// One-time cleanup of pre-fix Run values. If the old shared "Cuckoo" (or the
    /// pre-rename "TwitchDropsMiner") value points at THIS instance's exe, convert it
    /// into the per-instance form so this instance keeps auto-starting and no longer
    /// collides with others.
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
                return;
            foreach (string legacyName in new[] { LegacySharedValue, LegacyOldNameValue })
            {
                if (key.GetValue(legacyName) is not string command)
                    continue;
                // only claim the entry if it launches this very install
                if (!command.Contains(ExePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool tray = command.Contains("--tray", StringComparison.OrdinalIgnoreCase);
                key.DeleteValue(legacyName, throwOnMissingValue: false);
                key.SetValue(RegistryValueName, RegistryCommand(tray));
                Logger.Instance.Info(
                    $"Autostart migrated the legacy '{legacyName}' entry to a per-instance entry");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Exception("Autostart legacy migration", ex);
        }
    }
}
