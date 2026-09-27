using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace UeDtLauncher;

public sealed class ReleaseSidecar
{
    public int SchemaVersion { get; set; } = 1;
    public string PackageFile { get; set; } = "";
    public long PackageSize { get; set; }
    public string PackageSha256 { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Environment { get; set; } = "prod";
    public string Channel { get; set; } = "stable";
    public string Platform { get; set; } = "windows-x64";
    public string PayloadRoot { get; set; } = ".";
    public string EntryPoint { get; set; } = "";
    public List<string> ExecutablePaths { get; set; } = new();
    public string? Notes { get; set; }
    public string? HeroPath { get; set; }
    public string? ThumbnailPath { get; set; }

    public string ReleaseId => string.Join("/", ProjectId, Environment, Channel, Version, Platform);

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported release.json schemaVersion.");
        Segment(ProjectId); Segment(Version);
        KnownValues.ValidateReleaseTuple(Platform, Environment, Channel);
        if (string.IsNullOrWhiteSpace(PackageFile) || PackageFile.Contains('/') || PackageFile.Contains('\\') ||
            PackageFile.Contains(':') || !PackageFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("packageFile must name a ZIP in the upload directory.");
        if (PackageSize <= 0 || !Regex.IsMatch(PackageSha256, "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("ZIP size and SHA-256 are required.");
        if (PayloadRoot != ".") Relative(PayloadRoot);
        Relative(EntryPoint);
        foreach (var path in ExecutablePaths) Relative(path);
        if (HeroPath is not null) Relative(HeroPath);
        if (ThumbnailPath is not null) Relative(ThumbnailPath);
    }

    public static void Segment(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
            throw new InvalidDataException("Invalid release identifier segment.");
    }

    public static string Relative(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || normalized.Split('/').Any(s =>
                s is "" or "." or ".." || s.EndsWith('.') || s.EndsWith(' ') ||
                s.Any(c => c < 32 || ":*?\"<>|".Contains(c)) ||
                Regex.IsMatch(s, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])([.]|$)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Invalid relative package path.");
        return normalized;
    }
}

public sealed record ZipIntakeLimits(long MaxExpandedBytes = 500L * 1024 * 1024 * 1024,
    int MaxFiles = 250_000, long MaxZipBytes = 200L * 1024 * 1024 * 1024);

public static class SidecarPackageValidator
{
    public static async Task<ReleaseSidecar> ReadAsync(string path, CancellationToken token = default)
    {
        var bytes = await ReadDocumentAsync(path, token);
        return Parse(bytes);
    }
    public static ReleaseSidecar Parse(byte[] bytes)
    {
        var metadata = System.Text.Json.JsonSerializer.Deserialize<ReleaseSidecar>(bytes, JsonFiles.Options)
            ?? throw new InvalidDataException("release.json is empty.");
        metadata.Validate();
        return metadata;
    }
    public static async Task<byte[]> ReadDocumentAsync(string path, CancellationToken token = default)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > 1024 * 1024) throw new InvalidDataException("release.json exceeds 1 MiB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static async Task GenerateAsync(string zipPath, string output, ReleaseSidecar metadata,
        CancellationToken token = default)
    {
        metadata.PackageFile = Path.GetFileName(zipPath);
        metadata.PackageSize = new FileInfo(zipPath).Length;
        metadata.PackageSha256 = await Hashing.Sha256FileAsync(zipPath, token);
        metadata.Validate();
        if (File.Exists(output)) throw new IOException("Sidecar already exists; choose a new output path.");
        await JsonFiles.WriteAsync(output, metadata, token);
    }

    public static async Task<string> ValidateAndExtractAsync(string zipPath, ReleaseSidecar metadata,
        string destination, ZipIntakeLimits? limits = null, CancellationToken token = default,
        IProgress<PackageWorkProgress>? progress = null, Func<string, long>? availableBytes = null)
    {
        metadata.Validate();
        limits ??= new();
        if (new FileInfo(zipPath).Length != metadata.PackageSize || metadata.PackageSize > limits.MaxZipBytes)
            throw new InvalidDataException("ZIP size mismatch or size limit exceeded.");
        progress?.Report(new("hash", 0, metadata.PackageSize, 0));
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hashBuffer = new byte[128 * 1024]; long hashed = 0; int read;
            while ((read = await input.ReadAsync(hashBuffer, token)) != 0)
            {
                hash.AppendData(hashBuffer, 0, read); hashed += read;
                progress?.Report(new("hash", hashed, metadata.PackageSize, 0));
            }
            if (hashed != metadata.PackageSize || !Convert.ToHexString(hash.GetHashAndReset()).Equals(metadata.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP SHA-256 mismatch.");
        }
        progress?.Report(new("archive-scan", 0, 0, 0));
        if (Directory.Exists(destination)) throw new IOException("Extraction destination must be new.");
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > limits.MaxFiles) throw new InvalidDataException("ZIP entry limit exceeded.");
        var comparer = metadata.Platform == "windows-x64" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = new Dictionary<string, bool>(comparer);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var relative = ReleaseSidecar.Relative(entry.FullName.TrimEnd('/', '\\'));
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type != 0 && type != 0x8000 && type != 0x4000)
                throw new InvalidDataException("Links and special ZIP entries are not allowed.");
            if (!entries.TryAdd(relative, directory)) throw new InvalidDataException("Duplicate ZIP path.");
            expanded = checked(expanded + entry.Length);
            if (expanded > limits.MaxExpandedBytes) throw new InvalidDataException("Expanded ZIP size limit exceeded.");
        }
        foreach (var path in entries.Keys)
        {
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
                if (entries.TryGetValue(string.Join('/', parts.Take(i)), out var directory) && !directory)
                    throw new InvalidDataException("ZIP file/directory collision.");
        }
        var prefix = metadata.PayloadRoot == "." ? "" : ReleaseSidecar.Relative(metadata.PayloadRoot) + "/";
        foreach (var required in metadata.ExecutablePaths.Append(metadata.EntryPoint)
                     .Concat(new[] { metadata.HeroPath, metadata.ThumbnailPath }.OfType<string>()))
            if (!entries.TryGetValue(prefix + ReleaseSidecar.Relative(required), out var directory) || directory)
                throw new InvalidDataException("Required file is absent from ZIP: " + required);

        var estimate = IntakeDiskSpace.Estimate(expanded);
        progress?.Report(new("extract", 0, expanded, estimate));
        IntakeDiskSpace.Require(destination, estimate, availableBytes);
        Directory.CreateDirectory(destination);
        long actualTotal = 0;
        var buffer = new byte[128 * 1024];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = ReleaseSidecar.Relative(entry.FullName.TrimEnd('/', '\\'));
            var target = SafePath.ResolveInsideChecked(destination, relative);
            if (entries[relative]) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                written += count; actualTotal = checked(actualTotal + count);
                if (written > entry.Length || actualTotal > limits.MaxExpandedBytes)
                    throw new InvalidDataException("ZIP expanded beyond declared limits.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                progress?.Report(new("extract", actualTotal, expanded, IntakeDiskSpace.Estimate(expanded - actualTotal)));
            }
            if (written != entry.Length) throw new InvalidDataException("Truncated ZIP entry.");
        }
        return metadata.PayloadRoot == "." ? destination : SafePath.ResolveInside(destination, metadata.PayloadRoot);
    }
}
