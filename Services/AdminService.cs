using System.Security.Principal;

namespace USBShare.Services;

public interface IAdminService
{
    bool IsRunningAsAdministrator();
}

public sealed class AdminService : IAdminService
{
    public bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
