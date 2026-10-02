using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using UeDtLauncher;

// Local test process only. No network, company applications, fault switches in product binaries.
internal static class RuntimeDataSmoke
{
    internal static void Payload(string[] args)
    {
        var user = args.Single(a => a.StartsWith("-UserDir=", StringComparison.Ordinal))[9..];
        var log = args.Single(a => a.StartsWith("-abslog=", StringComparison.Ordinal))[8..];
        File.WriteAllText(Path.Combine(user, "payload-data.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, userDir = user }));
        File.WriteAllText(log, "owned runtime data payload ended normally\n");
    }

    internal static async Task LaunchAsync(LauncherConfig config, string launcher)
    {
        var ticket = RuntimeStore.Begin(config, RuntimeIdentities.Current(), launcher);
        using var host = Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "runtime-host" } })!;
        await host.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new RuntimeHostSession(config, ticket, null, config.SelectedRelease), JsonFiles.Options).Replace("\r", "").Replace("\n", ""));
        host.StandardInput.Close();
        var output = host.StandardOutput.ReadToEndAsync(); var error = host.StandardError.ReadToEndAsync();
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (host.ExitCode != 0 || !(await output).Contains("\"state\":\"started\"", StringComparison.Ordinal)) throw new IOException("Published host failed: " + await error);
        if (RuntimeStore.Observe(config).State != RuntimeState.Quiescent) throw new InvalidDataException("Host did not confirm normal completion.");
    }

    internal static async Task RunAsync(string[] args)
    {
        var root = Path.GetFullPath(args[0]); var launcher = Path.GetFullPath(args[1]);
        if (Directory.Exists(root) || !File.Exists(launcher)) throw new InvalidOperationException("New root and published launcher are required.");
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); var sid = WindowsIdentity.GetCurrent().User!; acl.SetOwner(sid);
            foreach (var allowed in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(allowed, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(acl);
        }
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var results = new List<object>();
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var c = new LauncherConfig { SchemaVersion = 3, ProjectId = "data-smoke", Environment = "dev", DistributionServerUrl = "https://unused.invalid",
                InstallDir = Path.Combine(root, "apps"), VersionedInstallRoot = Path.Combine(root, "apps"), StateRootDir = Path.Combine(root, "state"), LogDir = Path.Combine(root, "logs"),
                RuntimeData = new() { Enabled = true, RootDirectory = Path.Combine(root, "data") }, LaunchArguments = ["runtime-data-payload"], LaunchAfterUpdate = false };
            VersionedReleasePaths.Bind(c, new("data-smoke", "dev", "stable", c.TargetPlatform, version));
            RuntimeStore.Initialize(c); Directory.CreateDirectory(c.InstallDir);
            var name = OperatingSystem.IsWindows() ? "payload.exe" : "payload";
            File.Copy(Environment.ProcessPath!, Path.Combine(c.InstallDir, name));
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(Path.Combine(c.InstallDir, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var manifest = new LauncherManifest { AppId = c.ProjectId!, Version = version, Platform = c.TargetPlatform, EntryPoint = name,
                Files = [new() { Path = name, Size = new FileInfo(Path.Combine(c.InstallDir, name)).Length, Sha256 = await Hashing.Sha256FileAsync(Path.Combine(c.InstallDir, name)) }] };
            await JsonFiles.WriteAsync(c.InstalledManifestPath, manifest);
            await LaunchAsync(c, launcher);
            var record = RuntimeStore.Read(c)!; var paths = RuntimeDataPolicy.Resolve(record.RuntimeData!);
            if (!File.Exists(Path.Combine(paths.UserDirectory, "payload-data.json")) || !File.Exists(paths.LogFile)) throw new IOException("Data/log path was not used by payload.");
            results.Add(new { version, runtime = "Quiescent", userDirectory = paths.UserDirectory, logFile = paths.LogFile, passed = true });
        }
        var json = JsonSerializer.Serialize(new { kind = "published-runtime-host-synthetic-data-smoke", results }, JsonFiles.Options);
        File.WriteAllText(Path.Combine(root, "result.json"), json); Console.WriteLine(json);
    }
}
