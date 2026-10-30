using System.Security.AccessControl;
using System.Security.Principal;

namespace LinkGate;

public sealed class PrivateStore
{
    public string DirectoryPath { get; }
    public string ProfilePath => Path.Combine(DirectoryPath, "profile.json");

    public PrivateStore(string? directory = null)
    {
        DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkGate", "private"));
        var current = new DirectoryInfo(DirectoryPath);
        for (var item = current; item is not null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new GatewayException("state_link");
        Directory.CreateDirectory(DirectoryPath);
        SecureDirectory(current);
    }

    public void Import(string source)
    {
        var profile = GatewayProfile.Load(source);
        Write("profile.json", profile.ToJson());
    }

    public string Write(string name, ReadOnlySpan<byte> content)
    {
        if (Path.GetFileName(name) != name || name.Contains(':')) throw new GatewayException("state_name");
        var target = Path.Combine(DirectoryPath, name);
        RejectLink(target);
        var temporary = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".new");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return target;
    }

    private static void RejectLink(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new GatewayException("state_link");
    }

    private static void SecureDirectory(DirectoryInfo directory)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new GatewayException("state_identity");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new DirectorySecurity();
        acl.SetOwner(user);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { user, system })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
}
