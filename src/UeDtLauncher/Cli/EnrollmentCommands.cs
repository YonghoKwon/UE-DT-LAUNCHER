using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private static async Task<int> RunEnrollmentAsync(string[] args)
    {
        var action = args.FirstOrDefault();
        var options = args.Skip(1).ToArray();
        var values = action switch
        {
            "prepare" => new[] { "--name", "--key-id", "--client", "--output", "--storage" },
            "finish" => new[] { "--server-url", "--project-id", "--platform", "--credential-name", "--signing-key-id", "--public-key", "--output", "--storage" },
            _ => throw new CliArgumentException("Use enroll prepare or enroll finish.")
        };
        for (var index = 0; index < options.Length; index++)
        {
            if (values.Contains(options[index], StringComparer.OrdinalIgnoreCase)) index++;
            else if (!options[index].Equals("--force", StringComparison.OrdinalIgnoreCase))
                throw new CliArgumentException("Unsupported enrollment option.");
        }

        var storage = Get(options, "--storage") ?? "managed";
        var layout = DeviceCredentials.StorageLayout(storage);
        return action == "prepare" ? await PrepareEnrollmentAsync(options, layout) : await FinishEnrollmentAsync(options, storage);
    }

    private static async Task<int> PrepareEnrollmentAsync(string[] args, ManagedLauncherPathLayout? layout)
    {
        var name = Required(args, "--name");
        var keyId = DeviceCredentials.ValidateIdentifier(Required(args, "--key-id"));
        ReleaseSidecar.Segment(keyId);
        var client = Required(args, "--client");
        ReleaseSidecar.Segment(client);
        var output = Path.Combine(Path.GetFullPath(Required(args, "--output")), client + ".enroll.json");
        var overwrite = Has(args, "--force");
        RefuseExistingEnrollmentOutput(output, overwrite);

        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(entry => entry.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Where(address => !(address.GetAddressBytes() is [169, 254, _, _]))
            .Select(address => address.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string publicPem;
        try { publicPem = EnrollmentPublicKey(name, keyId, layout); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException or JsonException or InvalidDataException)
        {
            // OS and JSON errors can contain the credential path. Keep this diagnostic local and fixed.
            Console.Error.WriteLine("Device credential is unavailable, unsafe, or belongs to a different key ID. Check permissions and the requested key ID.");
            return 1;
        }

        await JsonFiles.WriteAsync(output, new DeviceEnrollmentBundle(1, client, keyId, publicPem,
            Dns.GetHostName(), addresses, DateTimeOffset.UtcNow), overwrite);
        Console.WriteLine("Public enrollment bundle written. Transfer it to the distribution server administrator.");
        return 0;
    }

    private static string EnrollmentPublicKey(string name, string keyId, ManagedLauncherPathLayout? layout)
    {
        var path = DeviceCredentials.PathFor(name, layout);
        if (!File.Exists(path))
        {
            try { return DeviceCredentials.Generate(name, keyId, layout).PublicKeyPem; }
            catch (IOException) when (File.Exists(path)) { /* Another prepare published the credential. */ }
        }
        var existing = DeviceCredentials.Read(name, layout);
        if (!string.Equals(existing.KeyId, keyId, StringComparison.Ordinal))
            throw new InvalidDataException("Existing device credential has a different key ID.");
        using var key = ECDsa.Create();
        key.ImportFromPem(existing.PrivateKeyPem);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    private static async Task<int> FinishEnrollmentAsync(string[] args, string storage)
    {
        var output = Path.GetFullPath(Required(args, "--output"));
        var reportPath = output + ".doctor.json";
        var overwrite = Has(args, "--force");
        RefuseExistingEnrollmentOutput(output, overwrite);
        RefuseExistingEnrollmentOutput(reportPath, overwrite);
        var managed = storage == "managed";
        var suppliedKey = Required(args, "--public-key");
        var publicPath = managed ? ManagedEnrollmentTrust.FixedPath(suppliedKey, ManagedLauncherPathLayout.SystemDefault().ConfigRoot) : Path.GetFullPath(suppliedKey);
        var extension = Path.GetExtension(output.TrimEnd(' ', '.'));
        if (string.Equals(publicPath, output, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(publicPath, reportPath, StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".keycred", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cred", StringComparison.OrdinalIgnoreCase))
            throw new CliArgumentException("Use a separate output for enrollment configuration.");
        using (var stream = managed ? ManagedEnrollmentTrust.OpenVerified(publicPath) : File.OpenRead(publicPath))
        using (var key = ECDsa.Create())
        {
            if (stream.Length > 4096) throw new InvalidDataException("Expected a release signing public key PEM.");
            using var reader = new StreamReader(stream);
            var pem = (await reader.ReadToEndAsync()).Trim();
            if (!PemEncoding.TryFind(pem, out var fields) || fields.Location.GetOffsetAndLength(pem.Length) != (0, pem.Length) ||
                !pem.AsSpan()[fields.Label].SequenceEqual("PUBLIC KEY"))
                throw new InvalidDataException("Expected a release signing public key PEM.");
            key.ImportFromPem(pem);
        }

        var config = LauncherConfigurationTemplates.Distribution(new DistributionTemplateOptions(
            ServerUrl: Required(args, "--server-url"), ProjectId: Required(args, "--project-id"),
            Platform: Required(args, "--platform"), DeploymentMode: storage == "managed" ? "managed-agent" : "portable",
            CredentialName: Required(args, "--credential-name"), SigningKeyId: Required(args, "--signing-key-id"), PublicKeyPath: publicPath));
        await JsonFiles.WriteAsync(output, config, overwrite);

        // In managed mode the existing doctor goes through the Agent. It cannot attest that
        // this config is the one loaded by the service, even if IPC returns a healthy result.
        var doctor = await LauncherDoctor.RunAsync(output, online: true);
        var sanitized = JsonSerializer.Deserialize<JsonElement>(DiagnosticRedactor.Redact(JsonSerializer.Serialize(doctor, JsonFiles.Options)));
        if (config.IsManagedDeployment)
        {
            await JsonFiles.WriteAsync(reportPath, new { Status = "deferred", Reason = "Agent not yet reloaded", Doctor = sanitized }, overwrite);
            Console.WriteLine("Agent configuration and deferred doctor report written. Have the administrator reload the update service and run doctor --online using the managed client configuration.");
            return 0;
        }
        await JsonFiles.WriteAsync(reportPath, sanitized, overwrite);
        Console.WriteLine(doctor.Healthy ? "Configuration written. Online doctor passed." : "Configuration written. Online doctor failed. See the saved report.");
        return doctor.Healthy ? 0 : 1;
    }

    private static void RefuseExistingEnrollmentOutput(string path, bool overwrite)
    {
        if (Directory.Exists(path) || !overwrite && File.Exists(path))
            throw new IOException("Enrollment output already exists. Use a new path or explicit --force for a file.");
    }
}
