using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UeDtLauncher;

public sealed record DevicePublicKey(int SchemaVersion, string KeyId, string PublicKeyPem);
public sealed record DevicePrivateKey(int SchemaVersion, string KeyId, string PrivateKeyPem);
public sealed record CredentialInspection(string Type, string? KeyId, bool Ready, string Message);

public static class DeviceCredentials
{
    public static string ValidateIdentifier(string value)
    {
        if (!Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Credential and key identifiers require 1-64 letters, digits, '.', '_' or '-'.");
        return value;
    }

    internal static string PathFor(string name, ManagedLauncherPathLayout? layout = null) =>
        Path.Combine((layout ?? ManagedLauncherPathLayout.Current()).CredentialRoot, ValidateIdentifier(name) + ".keycred");

    public static DevicePublicKey Generate(string name, string keyId, ManagedLauncherPathLayout? layout = null)
    {
        ValidateIdentifier(keyId);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DevicePrivateKey(1, keyId, key.ExportPkcs8PrivateKeyPem()), JsonFiles.Options);
        try { ProtectedCredentialFile.Write(PathFor(name, layout), bytes, managed: IsManaged(layout), replace: false); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return new DevicePublicKey(1, keyId, key.ExportSubjectPublicKeyInfoPem());
    }

    public static DevicePrivateKey Read(string name, ManagedLauncherPathLayout? layout = null)
    {
        var bytes = ProtectedCredentialFile.Read(PathFor(name, layout), IsManaged(layout));
        try
        {
            var value = JsonSerializer.Deserialize<DevicePrivateKey>(bytes, JsonFiles.Options)
                ?? throw new InvalidDataException("Invalid device credential.");
            ValidateIdentifier(value.KeyId);
            if (value.SchemaVersion != 1) throw new InvalidDataException("Unsupported device credential.");
            using var key = ECDsa.Create(); key.ImportFromPem(value.PrivateKeyPem);
            ValidateCurve(key);
            if (key.ExportParameters(true).D is null) throw new CryptographicException("Private key required.");
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void ValidatePublic(DevicePublicKey value)
    {
        ValidateIdentifier(value.KeyId);
        if (value.SchemaVersion != 1 || value.PublicKeyPem.Length > 4096 || value.PublicKeyPem.Contains("PRIVATE", StringComparison.Ordinal))
            throw new InvalidDataException("Only a schema 1 device public key may be registered.");
        using var key = ECDsa.Create(); key.ImportFromPem(value.PublicKeyPem); ValidateCurve(key);
    }

    internal static void ValidateCurve(ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new CryptographicException("Device keys must use P-256.");
    }

    internal static bool IsManaged(ManagedLauncherPathLayout? layout) => layout is null &&
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UE_DT_AGENT_DATA_ROOT"));

    public static CredentialInspection Inspect(string name, ManagedLauncherPathLayout? layout = null)
    {
        try
        {
            if (File.Exists(PathFor(name, layout)))
            {
                var key = Read(name, layout);
                return new("request-signature-v1", key.KeyId, true, "Protected device key is readable by this identity.");
            }
            var token = CredentialStore.Read(name, layout);
            return new("bearer", null, !string.IsNullOrEmpty(token), token is null ? "Credential is missing." : "Protected token is readable by this identity.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        { return new("unknown", null, false, "Credential format, ownership or access validation failed."); }
    }

    public static string RepairPermissions(string name, bool apply, ManagedLauncherPathLayout? layout = null)
    {
        var keyPath = PathFor(name, layout);
        var tokenPath = Path.ChangeExtension(keyPath, ".cred");
        var path = File.Exists(keyPath) ? keyPath : tokenPath;
        return ProtectedCredentialFile.RepairLinux(path, IsManaged(layout), apply);
    }
}
