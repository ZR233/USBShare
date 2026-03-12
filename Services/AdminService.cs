using System.Diagnostics;
using System.Security.Principal;

namespace USBShare.Services;

public interface IAdminService
{
    bool IsRunningAsAdministrator();
    bool TryRelaunchAsAdministrator(out string? errorMessage);
}

public sealed class AdminService : IAdminService
{
    public bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool TryRelaunchAsAdministrator(out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                errorMessage = "无法获取当前程序路径。";
                return false;
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(processStartInfo);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
