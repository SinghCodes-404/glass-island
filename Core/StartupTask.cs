using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace GlassIsland.Core;

/// <summary>
/// Starts Glass Island when the user signs in, through Task Scheduler. More reliable than the registry "Run" list,
/// which Windows sometimes skips entirely at sign-in. Per-user task, no admin rights needed.
/// </summary>
public static class StartupTask
{
    const string Name = "GlassIsland";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled => Schtasks($"/Query /TN \"{Name}\"") == 0;

    public static bool Enable(string exePath)
    {
        string user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        string exe = SecurityElement.Escape(exePath);
        // Laptop-friendly: also start on battery, never stop for battery, and no 3-day run limit (Windows' defaults).
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Glass Island when you sign in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT3S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <StartWhenAvailable>true</StartWhenAvailable>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"glassisland-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            bool ok = Schtasks($"/Create /TN \"{Name}\" /XML \"{file}\" /F") == 0;
            if (ok) RemoveRunEntry();   // one mechanism only
            else App.Log("Could not create the sign-in task.");
            return ok;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    public static void Disable()
    {
        Schtasks($"/Delete /TN \"{Name}\" /F");
        RemoveRunEntry();
    }

    /// <summary>Older versions used the registry Run list; move to the task.</summary>
    public static bool HasLegacyRunEntry()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("GlassIsland") != null;
    }

    static void RemoveRunEntry()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
        k?.DeleteValue("GlassIsland", false);
    }

    static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
