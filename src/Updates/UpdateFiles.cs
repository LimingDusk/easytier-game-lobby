using System.Security.AccessControl;
using System.Security.Principal;

namespace GameLobby;

internal static class UpdateFiles
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");
    private const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;

    internal static void DirectoryAt(string path, bool create)
    {
        var directory = new DirectoryInfo(path);
        if (create && !directory.Exists)
        {
            var security = new DirectorySecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(true, false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var principal in new[] { Administrators, SystemAccount })
                security.AddAccessRule(new(principal, FileSystemRights.FullControl, inheritance,
                    PropagationFlags.None, AccessControlType.Allow));
            // Suppress owners' implicit WRITE_DAC on files extracted by the .NET host.
            security.AddAccessRule(new(OwnerRights, FileSystemRights.ReadPermissions, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security);
        }
        RejectReparsePoint(directory);
        VerifyPermissions(directory.GetAccessControl(Sections));
    }

    internal static FileStream CreateFile(string path, FileMode mode)
    {
        if (mode == FileMode.OpenOrCreate && File.Exists(path)) VerifyFile(path);
        var security = new FileSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        foreach (var principal in new[] { Administrators, SystemAccount })
            security.AddAccessRule(new(principal, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(mode, FileSystemRights.Read | FileSystemRights.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous, security);
    }

    internal static void VerifyFile(string path)
    {
        var file = new FileInfo(path);
        RejectReparsePoint(file);
        VerifyPermissions(file.GetAccessControl(Sections));
    }

    private static void RejectReparsePoint(FileSystemInfo item)
    {
        if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("更新目录或文件使用了重解析点。");
    }

    private static bool IsAdministrator(IdentityReference? identity) =>
        Administrators.Equals(identity) || SystemAccount.Equals(identity);

    private static void VerifyPermissions(FileSystemSecurity security)
    {
        if (!IsAdministrator(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("更新目录或文件的所有者无法验证。");
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("更新目录或文件缺少访问控制。");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || IsAdministrator(rule.IdentityReference)) continue;
            if (!OwnerRights.Equals(rule.IdentityReference) ||
                (rule.FileSystemRights & ~(FileSystemRights.ReadPermissions | FileSystemRights.Synchronize)) != 0)
                throw new UnauthorizedAccessException("更新目录或文件的写入权限不安全。");
        }
    }
}
