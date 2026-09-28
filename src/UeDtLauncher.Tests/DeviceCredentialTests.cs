using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class DeviceCredentialTests
{
    [Fact]
    public void Key_RoundTrip_DistinctFromBearer_AndNoOverwrite()
    {
        using var fixture = new CredentialFixture();
        var publicKey = DeviceCredentials.Generate("device", "pc-01", fixture.Layout);
        Assert.DoesNotContain("PRIVATE", publicKey.PublicKeyPem);
        DeviceCredentials.ValidatePublic(publicKey);
        Assert.Null(CredentialStore.Read("device", fixture.Layout));
        var value = DeviceCredentials.Read("device", fixture.Layout);
        Assert.Equal("pc-01", value.KeyId);
        using var signer = ECDsa.Create(); signer.ImportFromPem(value.PrivateKeyPem);
        using var verifier = ECDsa.Create(); verifier.ImportFromPem(publicKey.PublicKeyPem);
        byte[] message = [1, 2, 3];
        Assert.True(verifier.VerifyData(message, signer.SignData(message, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
        Assert.Throws<IOException>(() => DeviceCredentials.Generate("device", "replacement", fixture.Layout));
        Assert.Equal(value, DeviceCredentials.Read("device", fixture.Layout));
        Assert.True(DeviceCredentials.Inspect("device", fixture.Layout).Ready);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("name\n")]
    [InlineData("/absolute")]
    [InlineData("")]
    public void InvalidIdentifiersAreRejected(string name) => Assert.Throws<ArgumentException>(() => DeviceCredentials.ValidateIdentifier(name));

    [Fact]
    public void PublicRegistration_RejectsPrivateAndWrongCurve()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => DeviceCredentials.ValidatePublic(new(1, "id", key.ExportPkcs8PrivateKeyPem())));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<CryptographicException>(() => DeviceCredentials.ValidatePublic(new(1, "id", other.ExportSubjectPublicKeyInfoPem())));
    }

    [Fact]
    public void Schema3_RequiresExplicitSignedDistributionProfile()
    {
        var config = new LauncherConfig { SchemaVersion = 3, DistributionServerUrl = "http://127.0.0.1:18500", CatalogUrl = "http://127.0.0.1:18500/api/v1/catalog", RequireSignedManifests = true };
        config.Security.AuthenticationMode = "request-signature-v1";
        config.Security.RequireHttps = false;
        config.Security.CredentialName = "device";
        config.Security.TrustedSigningKeys.Add(new() { KeyId = "release", PublicKeyPath = "public.pem" });
        LauncherConfigValidator.Validate(config);
        config.SchemaVersion = 2;
        Assert.Throws<InvalidOperationException>(() => LauncherConfigValidator.Validate(config));
        config.SchemaVersion = 3; config.Security.EnforceCatalogFreshness = false;
        Assert.Throws<InvalidOperationException>(() => LauncherConfigValidator.Validate(config));
        config.Security.EnforceCatalogFreshness = true; config.Security.TrustedSigningKeys.Clear();
        Assert.Throws<InvalidOperationException>(() => LauncherConfigValidator.Validate(config));
    }

    [Fact]
    public void ExcessivePermissionsAreRejected_OriginalCanBeRepairedExplicitly()
    {
        using var fixture = new CredentialFixture();
        DeviceCredentials.Generate("device", "id", fixture.Layout);
        var path = Path.Combine(fixture.Layout.CredentialRoot, "device.keycred");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Assert.False(DeviceCredentials.Inspect("device", fixture.Layout).Ready);
            DeviceCredentials.RepairPermissions("device", false, fixture.Layout);
            Assert.False(DeviceCredentials.Inspect("device", fixture.Layout).Ready);
            DeviceCredentials.RepairPermissions("device", true, fixture.Layout);
            Assert.True(DeviceCredentials.Inspect("device", fixture.Layout).Ready);
        }
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileInfo(path).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
            Assert.False(DeviceCredentials.Inspect("device", fixture.Layout).Ready);
        }
    }

    [Fact]
    public void Linux_LinksAndWritableDirectoryAreRejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new CredentialFixture();
        DeviceCredentials.Generate("device", "id", fixture.Layout);
        File.CreateSymbolicLink(Path.Combine(fixture.Layout.CredentialRoot, "alias.keycred"), "device.keycred");
        Assert.False(DeviceCredentials.Inspect("alias", fixture.Layout).Ready);
        File.SetUnixFileMode(fixture.Layout.CredentialRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        Assert.False(DeviceCredentials.Inspect("device", fixture.Layout).Ready);
    }

    private sealed class CredentialFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "uedt-key-" + Guid.NewGuid().ToString("N"));
        public ManagedLauncherPathLayout Layout { get; }
        public CredentialFixture()
        {
            Directory.CreateDirectory(Root);
            Layout = new(Root, Root, Root, Root, Root, Path.Combine(Root, "credentials"));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
