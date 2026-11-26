using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace UeDtLauncher.Distribution;

internal static class ClientEnrollment
{
    private static readonly JsonSerializerOptions PlanJson = new(JsonFiles.Options)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.HangulSyllables)
    };

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Action? beforePolicyReplace = null, Action? beforeWriterLease = null)
    {
        try
        {
            var options = EnrollmentOptions.Parse(args);
            var settings = await JsonFiles.ReadAsync<DistributionSettings>(options.Config);
            var inputs = await EnrollmentInputs.ReadAsync(options);
            var store = IntakeStore.OpenForEnrollmentValidation(settings);
            var keys = new DistributionDeviceKeys(store);
            var policyPath = Path.GetFullPath(settings.PolicyPath);
            var plan = await BuildPlanAsync(policyPath, keys, inputs);
            if (options.DryRun || !plan.HasChanges)
            {
                await WritePlanAsync(output, plan, options.DryRun);
                return 0;
            }

            beforeWriterLease?.Invoke();
            using (var writer = store.LockWriter())
            {
                // Validate against the current keys while holding the same writer lease as client-key add.
                plan = await BuildPlanAsync(policyPath, keys, inputs);
                if (plan.HasChanges)
                    await ApplyAsync(plan, policyPath, store.Credentials, writer, beforePolicyReplace);
            }
            await WritePlanAsync(output, plan, dryRun: false);
            return 0;
        }
        catch (EnrollmentInputException invalid)
        {
            await error.WriteLineAsync("Enrollment rejected:");
            foreach (var issue in invalid.Issues) await error.WriteLineAsync("- " + issue);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or System.Data.Common.DbException or PlatformNotSupportedException)
        {
            await error.WriteLineAsync("Enrollment could not finish. Check the initialized server store, access policy and permissions. If apply was interrupted, repeat the same command to finish.");
            return 1;
        }
    }

    private static async Task<EnrollmentPlan> BuildPlanAsync(string policyPath, DistributionDeviceKeys keys, EnrollmentInputs inputs)
    {
        var errors = new List<string>(inputs.Errors);
        var original = await ReadPolicyAsync(policyPath);
        AccessPolicy existingPolicy;
        try { existingPolicy = ParsePolicy(original); }
        catch (EnrollmentInputException invalid)
        {
            errors.AddRange(invalid.Issues);
            throw new EnrollmentInputException(errors);
        }
        var clientIds = existingPolicy.Clients.Select(client => client.Id).ToHashSet(StringComparer.Ordinal);
        // Find can latch expiration and write audit state. List inspects metadata without mutating it.
        var registered = keys.List().ToDictionary(key => key.KeyId, StringComparer.Ordinal);
        var rows = new List<EnrollmentRow>();
        var clientsToAdd = new List<ClientAccess>();
        foreach (var candidate in inputs.Candidates)
        {
            var bundle = candidate.Bundle;
            var old = registered.GetValueOrDefault(bundle.KeyId);
            if (old is not null && (old.ClientId != bundle.ClientId || old.PublicKeyPem != bundle.PublicKeyPem))
            {
                errors.Add($"Bundle {candidate.Number}: key ID is already registered to a different client or public key.");
                continue;
            }
            var keepPolicy = clientIds.Contains(bundle.ClientId);
            if (!keepPolicy)
                clientsToAdd.Add(new ClientAccess { Id = bundle.ClientId, Addresses = candidate.Addresses, Grants = inputs.Grants });
            rows.Add(new(candidate, old is null, keepPolicy));
        }
        if (errors.Count != 0) throw new EnrollmentInputException(errors);
        if (clientsToAdd.Count == 0) return new(rows, original, null);

        var merged = AppendClients(original, clientsToAdd);
        _ = ParsePolicy(merged);
        return new(rows, original, merged);
    }

    private static byte[] AppendClients(byte[] original, List<ClientAccess> additions)
    {
        // Carry forward original properties and client objects, including administrator metadata.
        using var document = JsonDocument.Parse(original);
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        var appended = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals("clients", StringComparison.OrdinalIgnoreCase)) property.WriteTo(writer);
            else
            {
                WriteClients(property.Name, property.Value.EnumerateArray());
                appended = true;
            }
        }
        if (!appended) WriteClients("clients", []);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.ToArray();

        void WriteClients(string name, IEnumerable<JsonElement> existing)
        {
            writer.WriteStartArray(name);
            foreach (var client in existing) client.WriteTo(writer);
            foreach (var client in additions) JsonSerializer.Serialize(writer, client, JsonFiles.Options);
            writer.WriteEndArray();
        }
    }

    private static AccessPolicy ParsePolicy(byte[] bytes)
    {
        if (bytes.Length > FileAccessPolicyProvider.MaxPolicyBytes)
            throw new EnrollmentInputException(["Access policy exceeds the allowed size."]);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
                    .Count(property => property.Name.Equals("clients", StringComparison.OrdinalIgnoreCase)) > 1)
                throw new InvalidDataException("Invalid policy root.");
            var policy = document.Deserialize<AccessPolicy>(JsonFiles.Options) ?? throw new InvalidDataException("Missing policy.");
            // Same DTO and compiler as FileAccessPolicyProvider, without creating a file for dry-run.
            _ = CompiledAccessPolicy.Create(policy);
            return policy;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or FormatException)
        {
            throw new EnrollmentInputException(["Access policy failed validation."]);
        }
    }

    private static async Task<byte[]> ReadPolicyAsync(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            await using var stream = LinuxFileAccess.OpenRegularRead(path);
            return await EnrollmentInputs.ReadBoundedAsync(stream, FileAccessPolicyProvider.MaxPolicyBytes);
        }
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Policy must be a regular file.");
        return await EnrollmentInputs.ReadBoundedAsync(path, FileAccessPolicyProvider.MaxPolicyBytes);
    }

    private static async Task ApplyAsync(EnrollmentPlan plan, string policyPath, CredentialAuthority credentials,
        Authority.AuthorityStore.WriterLease writer, Action? beforePolicyReplace)
    {
        string? temporary = null;
        LinuxFileAccess.Metadata? originalAccess = null;
        try
        {
            if (plan.MergedPolicy is not null)
            {
                if (OperatingSystem.IsLinux()) originalAccess = ReadLinuxPolicyAccess(policyPath);
                temporary = Path.Combine(Path.GetDirectoryName(policyPath)!, $".client-enrollment-{Guid.NewGuid():N}.tmp");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
                if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    await stream.WriteAsync(plan.MergedPolicy);
                    if (OperatingSystem.IsLinux()) PreserveLinuxPolicyAccess(stream, originalAccess!);
                    stream.Flush(flushToDisk: true);
                }
                _ = await new FileAccessPolicyProvider(temporary).LoadAsync(CancellationToken.None);
                await VerifyPolicyReplacementAsync(plan, policyPath, temporary, originalAccess);
            }

            var registrations = plan.Rows.Where(row => row.AddKey).Select(row =>
            {
                var bundle = row.Candidate.Bundle;
                return (bundle.ClientId, new DevicePublicKey(bundle.SchemaVersion, bundle.KeyId, bundle.PublicKeyPem), (DateTimeOffset?)null);
            });
            credentials.AddDeviceKeys(registrations, writer);
            if (temporary is null) return;
            beforePolicyReplace?.Invoke();
            await VerifyPolicyReplacementAsync(plan, policyPath, temporary, originalAccess);
            if (OperatingSystem.IsWindows()) File.Replace(temporary, policyPath, destinationBackupFileName: null);
            else File.Move(temporary, policyPath, overwrite: true);
        }
        finally
        {
            if (temporary is not null) File.Delete(temporary);
        }
    }

    private static async Task VerifyPolicyReplacementAsync(EnrollmentPlan plan, string policyPath, string temporary, LinuxFileAccess.Metadata? originalAccess)
    {
        if (!(await ReadPolicyAsync(policyPath)).AsSpan().SequenceEqual(plan.OriginalPolicy))
            throw new IOException("The policy changed during enrollment.");
        if (OperatingSystem.IsLinux())
        {
            var source = ReadLinuxPolicyAccess(policyPath);
            if (source.File != originalAccess!.File || !originalAccess.HasSameAccess(source) ||
                !originalAccess.HasSameAccess(ReadLinuxPolicyAccess(temporary)))
                throw new IOException("The policy ownership or permissions changed during enrollment.");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static LinuxFileAccess.Metadata ReadLinuxPolicyAccess(string path)
    {
        try
        {
            using var stream = LinuxFileAccess.OpenRegularRead(path);
            return LinuxFileAccess.ReadMetadata(stream.SafeFileHandle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new EnrollmentInputException(["Could not verify Linux policy ownership, group, mode or ACL."]); }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void PreserveLinuxPolicyAccess(FileStream staged, LinuxFileAccess.Metadata original)
    {
        try { LinuxFileAccess.PreserveAccess(staged.SafeFileHandle, original); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new EnrollmentInputException(["Could not preserve Linux policy ownership, group, mode or ACL."]); }
    }

    private static Task WritePlanAsync(TextWriter output, EnrollmentPlan plan, bool dryRun) => output.WriteLineAsync(
        JsonSerializer.Serialize(new
        {
            DryRun = dryRun,
            KeysToAdd = plan.Rows.Count(row => row.AddKey),
            PoliciesToAdd = plan.Rows.Count(row => !row.KeepPolicy),
            Clients = plan.Rows.Select(row => new
            {
                row.Candidate.Bundle.ClientId,
                row.Candidate.Bundle.KeyId,
                Key = row.AddKey ? "add" : "existing key retained",
                Policy = row.KeepPolicy ? "기존 항목 유지" : "add"
            })
        }, PlanJson));

    private sealed record EnrollmentRow(EnrollmentCandidate Candidate, bool AddKey, bool KeepPolicy);
    private sealed record EnrollmentPlan(List<EnrollmentRow> Rows, byte[] OriginalPolicy, byte[]? MergedPolicy)
    {
        internal bool HasChanges => MergedPolicy is not null || Rows.Any(row => row.AddKey);
    }
}
