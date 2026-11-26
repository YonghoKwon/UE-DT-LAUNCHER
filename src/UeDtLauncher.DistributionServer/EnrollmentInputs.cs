using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher.Distribution;

internal sealed class EnrollmentInputException(IReadOnlyList<string> issues) : Exception("Enrollment inputs failed validation.")
{
    internal IReadOnlyList<string> Issues { get; } = issues;
}

internal sealed record EnrollmentOptions(string Config, string Folder, string AddressSource, List<string> Grants, bool DryRun)
{
    internal static EnrollmentOptions Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var grants = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--error-details") continue;
            if (option == "--dry-run" && options.TryAdd(option, "")) continue;
            if (option is not ("--config" or "--from" or "--grant" or "--address-source"))
                throw new EnrollmentInputException(["Unknown or duplicate option. See client-enroll --help."]);
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new EnrollmentInputException([$"Value required after {option}."]);
            var value = args[++index];
            if (option == "--grant") grants.Add(value);
            else if (!options.TryAdd(option, value)) throw new EnrollmentInputException([$"Duplicate option {option}."]);
        }
        if (!options.TryGetValue("--config", out var config) || !options.TryGetValue("--from", out var folder) || grants.Count == 0)
            throw new EnrollmentInputException(["Provide --config, --from and at least one --grant."]);
        var source = options.GetValueOrDefault("--address-source", "observed");
        if (source != "observed" && !(source.StartsWith("file:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(source[5..])))
            throw new EnrollmentInputException(["--address-source must be observed or file:<csv>."]);
        return new(config, folder, source, grants, options.ContainsKey("--dry-run"));
    }
}

internal sealed record EnrollmentCandidate(int Number, DeviceEnrollmentBundle Bundle, List<string> Addresses);

internal sealed record EnrollmentInputs(List<EnrollmentCandidate> Candidates, List<ReleaseGrant> Grants, List<string> Errors)
{
    private const int MaxBundles = 1000;

    internal static async Task<EnrollmentInputs> ReadAsync(EnrollmentOptions options)
    {
        var errors = new List<string>();
        var grants = ParseGrants(options.Grants, errors);
        var csv = options.AddressSource == "observed" ? null : await ReadAddressesAsync(options.AddressSource[5..], errors);
        var entries = Directory.EnumerateFiles(options.Folder, "*.enroll.json").Take(MaxBundles + 1).Order(StringComparer.Ordinal).ToArray();
        if (entries.Length is 0 or > MaxBundles)
            throw new EnrollmentInputException([$"Input folder must contain 1-{MaxBundles} enrollment bundles."]);

        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<EnrollmentCandidate>();
        for (var index = 0; index < entries.Length; index++)
        {
            var number = index + 1;
            try
            {
                var bundle = await ReadBundleAsync(entries[index]);
                if (!keyIds.Add(bundle.KeyId)) errors.Add($"Bundle {number}: duplicate key ID.");
                if (!clientIds.Add(bundle.ClientId)) errors.Add($"Bundle {number}: duplicate client ID.");
                var addresses = csv is null ? bundle.Ipv4Addresses.ToList() : csv.GetValueOrDefault(bundle.ClientId, []);
                if (addresses.Count == 0) errors.Add($"Bundle {number}: no address in the selected address source.");
                candidates.Add(new(number, bundle, addresses.Distinct(StringComparer.Ordinal).ToList()));
            }
            catch (EnrollmentInputException invalid)
            {
                errors.AddRange(invalid.Issues.Select(issue => $"Bundle {number}: {issue}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or CryptographicException or JsonException)
            {
                errors.Add($"Bundle {number}: invalid schema, identifiers or public key, or unreadable file.");
            }
        }
        return new(candidates, grants, errors);
    }

    private static async Task<DeviceEnrollmentBundle> ReadBundleAsync(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Linked bundle.");
        using var json = JsonDocument.Parse(await ReadBoundedAsync(path, 16 * 1024));
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid bundle schema.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (json.RootElement.EnumerateObject().Any(property => !names.Add(property.Name))) throw new InvalidDataException("Duplicate bundle field.");
        var bundle = json.Deserialize<DeviceEnrollmentBundle>(JsonFiles.Options) ?? throw new InvalidDataException("Missing bundle.");
        if (bundle.ClientId is null || bundle.KeyId is null || bundle.PublicKeyPem is null || bundle.Ipv4Addresses is null ||
            string.IsNullOrWhiteSpace(bundle.HostName) || bundle.HostName.Length > 255 || bundle.HostName.Any(char.IsControl) ||
            bundle.CreatedAtUtc == default || bundle.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Missing bundle fields.");

        ReleaseSidecar.Segment(bundle.ClientId);
        ReleaseSidecar.Segment(bundle.KeyId);
        if (!string.Equals(file.Name, bundle.ClientId + ".enroll.json", StringComparison.Ordinal))
            throw new EnrollmentInputException(["File name does not match the client ID."]);
        var pem = bundle.PublicKeyPem.Trim();
        if (!PemEncoding.TryFind(pem, out var fields) || fields.Location.GetOffsetAndLength(pem.Length) != (0, pem.Length) ||
            !pem.AsSpan()[fields.Label].SequenceEqual("PUBLIC KEY"))
            throw new InvalidDataException("Expected one public PEM.");
        DeviceCredentials.ValidatePublic(new(bundle.SchemaVersion, bundle.KeyId, bundle.PublicKeyPem));

        if (bundle.Ipv4Addresses.Any(value => !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.ToString() != value || IPAddress.IsLoopback(ip) || ip.GetAddressBytes() is [169, 254, _, _]))
            throw new EnrollmentInputException(["Invalid observed IPv4 address."]);
        return bundle;
    }

    private static List<ReleaseGrant> ParseGrants(List<string> values, List<string> errors)
    {
        var grants = new List<ReleaseGrant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var fields = values[index].Split(':');
            try
            {
                if (fields.Length != 3 || !KnownValues.Environments.Contains(fields[1]) || !KnownValues.Channels.Contains(fields[2]))
                    throw new InvalidDataException("Invalid grant.");
                ReleaseSidecar.Segment(fields[0]);
                if (seen.Add(values[index])) grants.Add(new ReleaseGrant { ProjectId = fields[0], Environment = fields[1], Channel = fields[2] });
            }
            catch (InvalidDataException) { errors.Add($"Grant {index + 1}: expected project:environment:channel with a known environment and channel."); }
        }
        return grants;
    }

    private static async Task<Dictionary<string, List<string>>> ReadAddressesAsync(string path, List<string> errors)
    {
        var addresses = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string content;
        try { content = new UTF8Encoding(false, true).GetString(await ReadBoundedAsync(path, 256 * 1024)); }
        catch (DecoderFallbackException)
        {
            errors.Add("Address CSV must use UTF-8.");
            return addresses;
        }
        using var reader = new StringReader(content);
        if (reader.ReadLine()?.TrimStart('\uFEFF') != "clientId,address")
        {
            errors.Add("Address CSV requires the header clientId,address.");
            return addresses;
        }
        var number = 1;
        while (reader.ReadLine() is { } row)
        {
            number++;
            if (string.IsNullOrWhiteSpace(row)) continue;
            var fields = row.Split(',', StringSplitOptions.TrimEntries);
            try
            {
                if (fields.Length != 2) throw new FormatException();
                var client = fields[0];
                ReleaseSidecar.Segment(client);
                var address = fields[1];
                var separator = address.IndexOf('/');
                var literal = separator < 0 ? address : address[..separator];
                if (!IPAddress.TryParse(literal, out var ip) || ip.ToString() != literal || literal.Contains('%'))
                    throw new FormatException();
                var network = AccessPolicyEvaluator.ParseNetwork(address);
                if (separator >= 0 && network.ToString() != address) throw new FormatException();
                if (!addresses.TryGetValue(client, out var list)) addresses.Add(client, list = []);
                list.Add(address);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException)
            {
                errors.Add($"Address CSV row {number}: expected client ID and canonical IP or CIDR.");
            }
        }
        return addresses;
    }

    internal static async Task<byte[]> ReadBoundedAsync(string path, int limit)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        return await ReadBoundedAsync(stream, limit);
    }

    internal static async Task<byte[]> ReadBoundedAsync(FileStream stream, int limit)
    {
        if (stream.Length > limit) throw new InvalidDataException("Enrollment input exceeds size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Enrollment input changed while reading.");
        return bytes;
    }
}
