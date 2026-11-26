using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ManagedEnrollmentTrustTests
{
    [Fact]
    public void ManagedPublicKeyRequiresAbsoluteFixedSystemLocation()
    {
        var fixedRoot = ManagedLauncherPathLayout.SystemDefault().ConfigRoot;
        var expected = Path.Combine(fixedRoot, "release-public.pem");
        Assert.Equal(expected, ManagedEnrollmentTrust.FixedPath(expected, fixedRoot));
        Assert.Throws<CliArgumentException>(() => ManagedEnrollmentTrust.FixedPath("release-public.pem", fixedRoot));
        Assert.Throws<CliArgumentException>(() => ManagedEnrollmentTrust.FixedPath(Path.Combine(Path.GetTempPath(), "release-public.pem"), fixedRoot));
        Assert.Throws<CliArgumentException>(() => ManagedEnrollmentTrust.FixedPath(Path.Combine(fixedRoot, "other.pem"), fixedRoot));
    }

    [Fact]
    public void WindowsPolicyAcceptsServiceReadAndRejectsNonAdminWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        VerifyWindowsPolicy();
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsPolicy()
    {
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var service = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var access = new FileSecurity();
        access.SetOwner(admin);
        access.AddAccessRule(new FileSystemAccessRule(admin, FileSystemRights.FullControl, AccessControlType.Allow));
        access.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Read, AccessControlType.Allow));

        ManagedEnrollmentTrust.ValidateWindows(access, checkWrite: true);
        access.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.WriteData, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => ManagedEnrollmentTrust.ValidateWindows(access, checkWrite: true));
    }
}
