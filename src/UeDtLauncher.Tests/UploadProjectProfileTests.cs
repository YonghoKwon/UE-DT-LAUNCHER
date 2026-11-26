using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class UploadProjectProfileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "project-upload-tests-" + Guid.NewGuid().ToString("N"));
    private const string Notes = "synthetic-release-notes-전체";
    private static readonly string[] Connection = ["--server", "user@host.example", "--remote-incoming", "/srv/incoming"];
    private static readonly string[] RequiredMetadata = ["--project-id", "demo", "--platform", "windows-x64", "--entry-point", "Demo.exe"];
    private static readonly string[] FullMetadata = [..RequiredMetadata, "--display-name", "Synthetic Demo", "--payload-root", "Windows",
        "--executable-paths", "Helper.exe", "--environment", "dev", "--channel", "beta", "--hero-path", "Hero.png",
        "--thumbnail-path", "Thumbnail.png"];

    [Theory]
    [InlineData("projectId", "--project-id", "other", "\"demo\"", "\"other\"", null)]
    [InlineData("displayName", "--display-name", "Other Demo", "\"Synthetic Demo\"", "\"Other Demo\"", "\"demo\"")]
    [InlineData("platform", "--platform", "linux-x64", "\"windows-x64\"", "\"linux-x64\"", null)]
    [InlineData("payloadRoot", "--payload-root", "Other", "\"Windows\"", "\"Other\"", "\".\"")]
    [InlineData("entryPoint", "--entry-point", "Other.exe", "\"Demo.exe\"", "\"Other.exe\"", null)]
    [InlineData("executablePaths", "--executable-paths", "Other.exe,Demo.exe", "[\"Helper.exe\"]", "[\"Other.exe\",\"Demo.exe\"]", "[]")]
    [InlineData("environment", "--environment", "prod", "\"dev\"", "\"prod\"", "\"prod\"")]
    [InlineData("channel", "--channel", "dev", "\"beta\"", "\"dev\"", "\"stable\"")]
    [InlineData("heroPath", "--hero-path", "Other.png", "\"Hero.png\"", "\"Other.png\"", "null")]
    [InlineData("thumbnailPath", "--thumbnail-path", "Other.png", "\"Thumbnail.png\"", "\"Other.png\"", "null")]
    public async Task CommandOverridesProfileAndOmittedFieldsUseLegacyDefaults(string field, string option, string value,
        string profileValue, string commandValue, string? defaultValue)
    {
        var path = await WriteProfileAsync();
        string[] args = ["--version", "1.0.0", "--project-profile", path];
        var fromProfile = await ReleaseMetadataInput.ReadAsync(args);
        var fromCommand = await ReleaseMetadataInput.ReadAsync([..args, option, value]);
        AssertField(fromProfile, field, profileValue, "project-profile");
        AssertField(fromCommand, field, commandValue, "command-line");
        var required = RequiredMetadata.Chunk(2).Where(pair => pair[0] != option).SelectMany(pair => pair);
        if (defaultValue is null)
            await Assert.ThrowsAsync<CliArgumentException>(() => ReleaseMetadataInput.ReadAsync(["--version", "1.0.0", ..required]));
        else
            AssertField(await ReleaseMetadataInput.ReadAsync(["--version", "1.0.0", ..required]), field, defaultValue, "default");
    }

    [Theory]
    [InlineData("missing-required", "--project-id")]
    [InlineData("conflict", "중 하나만")]
    [InlineData("unknown-field", "허용 필드")]
    [InlineData("secret-field", "허용 필드")]
    [InlineData("invalid-utf8", "올바른 UTF-8")]
    [InlineData("oversize-notes", "64 KiB")]
    [InlineData("missing-file", "--notes-file에")]
    [InlineData("duplicate-field", "허용 필드")]
    [InlineData("invalid-schema", "schemaVersion")]
    [InlineData("missing-zip", "--zip에")]
    [InlineData("missing-project-profile", "--project-profile에")]
    [InlineData("invalid-release-path", "ZIP 내부 경로")]
    public async Task InvalidInputExitsTwoBeforeLocalWorkOrRunner(string scenario, string expectedGuidance)
    {
        var zip = MakeZip();
        var profile = await WriteProfileAsync();
        var notes = Path.Combine(root, "note-path-marker.md");
        await File.WriteAllTextAsync(notes, Notes);
        var project = JsonNode.Parse(await File.ReadAllTextAsync(profile))!.AsObject();
        string[] extra = [];
        switch (scenario)
        {
            case "missing-required": project.Remove("projectId"); break;
            case "conflict": extra = ["--notes", Notes]; break;
            case "unknown-field": project["invalidFieldMarker"] = "invalid-value-marker"; break;
            case "secret-field": project["apiToken"] = "sensitive-value-marker"; break;
            case "invalid-utf8": await File.WriteAllBytesAsync(notes, [0xef, 0xbb, 0xbf, 0xc3, 0x28]); break;
            case "oversize-notes": await File.WriteAllBytesAsync(notes, new byte[65537]); break;
            case "missing-file": notes = Path.Combine(root, "missing-notes.md"); break;
            case "duplicate-field": project["ProjectId"] = "different"; break;
            case "invalid-schema": project["schemaVersion"] = 2; break;
            case "missing-zip": zip = Path.Combine(root, "missing.zip"); break;
            case "missing-project-profile": profile = Path.Combine(root, "missing-project-path-marker.json"); break;
            case "invalid-release-path": project["heroPath"] = "../private-path-marker"; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        if (scenario != "missing-project-profile") await File.WriteAllTextAsync(profile, project.ToJsonString());

        var runner = new RecordingRunner();
        var work = Path.Combine(root, "work");
        var sidecar = Path.Combine(root, "release.json");
        string[] args = ["--version", "1.0.0", "--zip", zip, "--project-profile", profile, "--notes-file", notes, ..extra];
        var before = Directory.GetFileSystemEntries(root).Order().ToArray();
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var error = await Assert.ThrowsAsync<CliArgumentException>(() =>
            Program.UploadAsync([..args, ..Connection], output, errors, runner, work));
        Assert.Contains(expectedGuidance, error.Message);
        foreach (var marker in new[] { root, Notes, "invalidFieldMarker", "sensitive-value-marker", "private-path-marker", notes })
            Assert.DoesNotContain(marker, error.Message);
        Assert.Equal(2, await Program.MainAsync(["upload", ..args, ..Connection]));
        Assert.Equal(2, await Program.MainAsync(["release-metadata", ..args, "--output", sidecar]));
        Assert.Equal(0, runner.AvailabilityChecks);
        Assert.Empty(runner.Calls);
        Assert.Equal(before, Directory.GetFileSystemEntries(root).Order());
        Assert.Empty(output.ToString());
        Assert.Empty(errors.ToString());
    }

    [Theory]
    [InlineData("missing-profile", "--profile")]
    [InlineData("invalid-profile-json", "--profile")]
    [InlineData("invalid-profile-setting", "--profile")]
    [InlineData("missing-zip-member", "ZIP 내부 경로")]
    public async Task InvalidUploadConnectionOrMemberFailsBeforeWorkOrRemoteCalls(string scenario, string expectedGuidance)
    {
        const string marker = "private-input-marker";
        var zip = MakeZip();
        var project = await WriteProfileAsync();
        var connection = Path.Combine(root, marker + ".json");
        var notes = Path.Combine(root, "notes.md");
        await File.WriteAllTextAsync(notes, Notes);
        if (scenario != "missing-profile")
        {
            var json = scenario switch
            {
                "invalid-profile-json" => """{"server":"private-input-marker", """,
                "invalid-profile-setting" => """{"server":"user@host.example","remoteIncoming":"private-input-marker"}""",
                _ => """{"server":"user@host.example","remoteIncoming":"/srv/incoming"}"""
            };
            await File.WriteAllTextAsync(connection, json);
        }
        if (scenario == "missing-zip-member")
        {
            var profile = JsonNode.Parse(await File.ReadAllTextAsync(project))!.AsObject();
            profile["heroPath"] = "Pictures/" + marker + ".png";
            await File.WriteAllTextAsync(project, profile.ToJsonString());
        }

        string[] args = ["--zip", zip, "--version", "1.0.0", "--project-profile", project, "--profile", connection, "--notes-file", notes];
        var runner = new RecordingRunner();
        var work = Path.Combine(root, "work");
        var before = Directory.GetFileSystemEntries(root).Order().ToArray();
        var error = await Assert.ThrowsAsync<CliArgumentException>(() =>
            Program.UploadAsync(args, TextWriter.Null, TextWriter.Null, runner, work));
        Assert.Contains(expectedGuidance, error.Message);
        Assert.DoesNotContain(marker, error.ToString());
        Assert.Equal(2, await Program.MainAsync(["upload", ..args]));
        Assert.Equal(0, runner.AvailabilityChecks);
        Assert.Empty(runner.Calls);
        Assert.Equal(before, Directory.GetFileSystemEntries(root).Order());
    }

    [Fact]
    public async Task DryRunJsonMasksHomeAndOtherLocalPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotEmpty(home);
        var work = Path.Combine(home, "upload-work");
        var zip = MakeZip();
        var profile = await WriteProfileAsync();
        var connection = Path.Combine(root, "connection.json");
        var identity = Path.Combine(root, "identity.key");
        await File.WriteAllTextAsync(identity, "synthetic identity");
        await JsonFiles.WriteAsync(connection, new UploadProfile("user@host.example", "/srv/incoming", IdentityFile: identity));
        using var output = new StringWriter();
        var runner = new RecordingRunner();
        Assert.Equal(0, await Program.UploadAsync(["--zip", zip, "--version", "1.0.0", "--project-profile", profile,
            "--profile", connection, "--upload-id", "safe-plan", "--dry-run", "--format", "json"], output, TextWriter.Null, runner, work));
        using var json = JsonDocument.Parse(output.ToString());
        var steps = json.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal("~/upload-work/safe-plan-NEW/payload.zip", LocalArgument(steps[1]));
        Assert.Equal("~/upload-work/safe-plan-NEW/release.json", LocalArgument(steps[2]));
        foreach (var step in steps)
        {
            var arguments = step.GetProperty("arguments").EnumerateArray().Select(arg => arg.GetString()).ToList();
            Assert.DoesNotContain(arguments, argument => argument!.Contains(home, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(identity, arguments);
            Assert.EndsWith("identity.key", arguments[arguments.IndexOf("-i") + 1]);
        }
        Assert.Equal(0, runner.AvailabilityChecks);
        Assert.Empty(runner.Calls);

        static string? LocalArgument(JsonElement step)
        {
            var args = step.GetProperty("arguments");
            return args[args.GetArrayLength() - 2].GetString();
        }
    }

    [Fact]
    public async Task DryRunAndMetadataGenerateIdenticalSidecarsFromBothInputForms()
    {
        var zip = MakeZip();
        var profile = await WriteProfileAsync();
        var notes = Path.Combine(root, "notes.md");
        await File.WriteAllTextAsync(notes, Notes, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        string[] args = ["--zip", zip, "--version", "1.0.0"];
        string[] fromCommand = [..args, ..FullMetadata, "--notes", Notes];
        string[] fromFiles = [..args, "--project-profile", profile, "--notes-file", notes];
        var runner = new RecordingRunner();
        var work = Path.Combine(root, "work");

        async Task<string> DryRun(string[] parameters)
        {
            using var output = new StringWriter();
            using var errors = new StringWriter();
            Assert.Equal(0, await Program.UploadAsync([..parameters, ..Connection, "--dry-run", "--format", "json"],
                output, errors, runner, work));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(Encoding.UTF8.GetByteCount(Notes), json.RootElement.GetProperty("project").GetProperty("notesBytes").GetInt32());
            Assert.DoesNotContain(Notes, output.ToString() + errors);
            return json.RootElement.GetProperty("sidecarSha256").GetString()!;
        }

        var planHash = await DryRun(fromCommand);
        Assert.Equal(planHash, await DryRun(fromFiles));
        Assert.False(Directory.Exists(work));
        Assert.Equal(0, runner.AvailabilityChecks);
        Assert.Empty(runner.Calls);

        var commandSidecar = Path.Combine(root, "command-release.json");
        var fileSidecar = Path.Combine(root, "file-release.json");
        Assert.Equal(0, await Program.MainAsync(["release-metadata", ..fromCommand, "--output", commandSidecar]));
        Assert.Equal(0, await Program.MainAsync(["release-metadata", ..fromFiles, "--output", fileSidecar]));
        Assert.Equal(planHash, await Hashing.Sha256FileAsync(commandSidecar));
        Assert.Equal(planHash, await Hashing.Sha256FileAsync(fileSidecar));
        Assert.Equal(Notes, (await SidecarPackageValidator.ReadAsync(fileSidecar)).Notes);

        using var uploadOutput = new StringWriter();
        Assert.Equal(0, await Program.UploadAsync([..fromFiles, ..Connection, "--keep-work"], uploadOutput, TextWriter.Null, runner, work));
        Assert.Equal(6, runner.Calls.Count);
        Assert.Equal(planHash, await Hashing.Sha256FileAsync(Directory.GetFiles(work, "release.json", SearchOption.AllDirectories).Single()));
        Assert.DoesNotContain(runner.Calls.SelectMany(arguments => arguments), argument =>
            argument.Contains(Notes) || argument.Contains(profile) || argument.Contains(notes) || argument.Contains("Synthetic Demo"));
    }

    [Fact]
    public async Task NotesFileAcceptsExactly64KiBIncludingBom()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "notes.md");
        var content = new string('a', 65536 - Encoding.UTF8.Preamble.Length);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var input = await ReleaseMetadataInput.ReadAsync([..RequiredMetadata, "--version", "1.0.0", "--notes-file", path]);
        Assert.Equal(content, input.Metadata.Notes);
        Assert.Equal(65536, new FileInfo(path).Length);
        Assert.Equal("notes-file", input.Sources["notes"]);
    }

    [Fact]
    public async Task SerializedSidecarLimitIsEnforcedBeforeWorkOrRunner()
    {
        var zip = MakeZip();
        var profile = Path.Combine(root, "project.json");
        var notes = Path.Combine(root, "notes.md");
        await JsonFiles.WriteAsync(profile, new UploadProjectProfile
        {
            ProjectId = "demo", Platform = "windows-x64", PayloadRoot = "Windows", EntryPoint = "Demo.exe",
            DisplayName = new string('한', 160000)
        });
        await File.WriteAllTextAsync(notes, new string('가', 21845));
        string[] args = ["--zip", zip, "--version", "1.0.0", "--project-profile", profile, "--notes-file", notes];
        var work = Path.Combine(root, "work");
        var runner = new RecordingRunner();
        var error = await Assert.ThrowsAsync<CliArgumentException>(() =>
            Program.UploadAsync([..args, ..Connection], TextWriter.Null, TextWriter.Null, runner, work));
        Assert.Contains("1 MiB", error.Message);
        var sidecar = Path.Combine(root, "release.json");
        Assert.Equal(2, await Program.MainAsync(["release-metadata", ..args, "--output", sidecar]));
        Assert.False(File.Exists(sidecar));
        Assert.False(Directory.Exists(work));
        Assert.Equal(0, runner.AvailabilityChecks);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ProjectInitDoesNotOverwriteAndConcurrentCallsHaveOneWinner()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "new.project.json");
        string[] args = ["project-init", "--output", path];
        Assert.Equal(0, await Program.UploadAsync(args, TextWriter.Null));
        var profile = UploadProjectProfile.Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal(1, profile.SchemaVersion);
        Assert.Null(profile.ProjectId);
        await File.WriteAllTextAsync(path, "existing-content");
        using var errors = new StringWriter();
        Assert.Equal(1, await Program.UploadAsync(args, TextWriter.Null, errors));
        Assert.Contains("--force", errors.ToString());
        Assert.Equal("existing-content", await File.ReadAllTextAsync(path));
        Assert.Equal(0, await Program.UploadAsync([..args, "--force"], TextWriter.Null));
        Assert.Equal(1, UploadProjectProfile.Parse(await File.ReadAllBytesAsync(path)).SchemaVersion);

        var raced = Path.Combine(root, "raced.project.json");
        async Task<bool> Create() =>
            await Program.UploadAsync(["project-init", "--output", raced], TextWriter.Null, TextWriter.Null) == 0;
        Assert.Single(await Task.WhenAll(Create(), Create()), success => success);
        Assert.Equal(1, UploadProjectProfile.Parse(await File.ReadAllBytesAsync(raced)).SchemaVersion);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    private async Task<string> WriteProfileAsync()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "demo.project.json");
        await JsonFiles.WriteAsync(path, new UploadProjectProfile
        {
            ProjectId = "demo", DisplayName = "Synthetic Demo", Platform = "windows-x64", PayloadRoot = "Windows",
            EntryPoint = "Demo.exe", ExecutablePaths = ["Helper.exe"], Environment = "dev", Channel = "beta",
            HeroPath = "Hero.png", ThumbnailPath = "Thumbnail.png"
        });
        return path;
    }

    private string MakeZip()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "payload.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in new[] { "Demo.exe", "Helper.exe", "Hero.png", "Thumbnail.png" })
        {
            using var stream = zip.CreateEntry("Windows/" + name).Open();
            stream.WriteByte(0);
        }
        return path;
    }

    private static void AssertField(ReleaseMetadataInput input, string field, string value, string source)
    {
        var metadata = JsonSerializer.SerializeToElement(input.Metadata, JsonFiles.Options);
        Assert.Equal(value, JsonSerializer.Serialize(metadata.GetProperty(field)));
        Assert.Equal(source, input.Sources[field]);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed class RecordingRunner : IUploadProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public int AvailabilityChecks { get; private set; }
        public bool IsAvailable(string executable) { AvailabilityChecks++; return true; }
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add(arguments);
            return Task.FromResult(0);
        }
    }
}
