using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UeDtLauncher;

public sealed record UploadProfile(string? Server = null, string? RemoteIncoming = null, int? Port = null, string? IdentityFile = null)
{
    public UploadProfile Override(UploadProfile command) => new(command.Server ?? Server,
        command.RemoteIncoming ?? RemoteIncoming, command.Port ?? Port, command.IdentityFile ?? IdentityFile);

    public void Validate()
    {
        if (Server is null || !Regex.IsMatch(Server, @"\A[A-Za-z0-9_][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9.-]*\z"))
            throw new ArgumentException("--server requires an allowlisted user@host.");
        if (RemoteIncoming is null || !RemoteIncoming.StartsWith('/') ||
            RemoteIncoming[1..].Split('/').Any(s => s is "" or "." or ".." || !Regex.IsMatch(s, @"\A[A-Za-z0-9._-]+\z")))
            throw new ArgumentException("--remote-incoming requires an absolute path with safe segments.");
        if (Port is < 1 or > 65535) throw new ArgumentException("--port must be 1..65535.");
        if (IdentityFile is not null && (!File.Exists(IdentityFile) || IdentityFile.Any(char.IsControl)))
            throw new ArgumentException("--identity-file must name an existing local file.");
    }

    public static Task InitializeAsync(string output, bool force) => JsonFiles.WriteAsync(output,
        new UploadProfile("upload-user@server.example", "/srv/ue-dt/incoming", 22), force);
}

public interface IUploadProcessRunner
{
    bool IsAvailable(string executable);
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token);
}

public sealed class OpenSshProcessRunner : IUploadProcessRunner
{
    private static string? FindExecutable(string executable)
    {
        var file = executable + (OperatingSystem.IsWindows() ? ".exe" : "");
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), file));
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
    public bool IsAvailable(string executable) => FindExecutable(executable) is not null;
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var resolved = FindExecutable(executable) ?? throw new UploadExecutableMissingException();
        var start = new ProcessStartInfo(resolved) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("OpenSSH process could not start.");
        // Drain without retaining remote text, which may contain secrets or arbitrary terminal control sequences.
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) != 0) { }
    }
}

public sealed record UploadStep(string Name, string Executable, IReadOnlyList<string> Arguments);
public sealed record UploadResult(bool Success, bool DryRun, string UploadId, string RemoteDirectory,
    string? WorkDirectory, string? FailedStage, IReadOnlyList<UploadStep> Steps, string Remaining, string NextAction,
    int? ExitCode = null, string? FailureKind = null, bool WorkPreserved = false, string? WorkCleanupWarning = null);

public sealed class ReleaseUploader
{
    private readonly IUploadProcessRunner runner;
    private readonly Func<string, long>? availableBytes;
    public ReleaseUploader(IUploadProcessRunner runner) : this(runner, null) { }
    internal ReleaseUploader(IUploadProcessRunner runner, Func<string, long>? availableBytes)
    { this.runner = runner; this.availableBytes = availableBytes; }

    public async Task<UploadResult> ExecuteAsync(string zip, ReleaseSidecar metadata, UploadProfile profile,
        string? uploadId, string workRoot, bool dryRun, CancellationToken token = default, Action<UploadProgress>? progress = null, bool keepWork = false)
    {
        profile.Validate();
        uploadId ??= $"{metadata.ProjectId}-{metadata.Version}-{metadata.Platform}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        ReleaseSidecar.Segment(uploadId);
        metadata = JsonSerializer.Deserialize<ReleaseSidecar>(JsonSerializer.Serialize(metadata, JsonFiles.Options), JsonFiles.Options)!;
        metadata.PackageFile = "payload.zip";
        metadata.PackageSize = new FileInfo(zip).Length;
        progress?.Invoke(new(1, "ZIP 검사 중", metadata.PackageSize));
        metadata.PackageSha256 = await Hashing.Sha256FileAsync(zip, token);
        await SidecarPackageValidator.ValidateArchiveAsync(zip, metadata, token);
        string? work = null;
        var localZip = Path.Combine(Path.GetFullPath(workRoot), uploadId + "-NEW", "payload.zip");
        var localJson = Path.Combine(Path.GetFullPath(workRoot), uploadId + "-NEW", "release.json");
        if (!dryRun && new[] { "ssh", "scp" }.Any(executable => !runner.IsAvailable(executable)))
            return new(false, false, uploadId, profile.RemoteIncoming + "/" + uploadId, null, "preflight", BuildSteps(profile, profile.RemoteIncoming + "/" + uploadId, localZip, localJson),
                "전송·작업 사본 생성 없음", UploadFailureGuidance.MissingExecutable, -1, "executable-missing");
        if (!dryRun)
        {
            try
            {
                IntakeDiskSpace.Require(workRoot, IntakeDiskSpace.Estimate(metadata.PackageSize), availableBytes);
                progress?.Invoke(new(2, "로컬 작업 사본 준비 중", metadata.PackageSize));
                work = Path.Combine(Path.GetFullPath(workRoot), uploadId + "-" + Guid.NewGuid().ToString("N"));
                work = UploadWork.CheckedPath(workRoot, work);
                if (Directory.Exists(work)) throw new IOException("Upload work folder already exists.");
                Directory.CreateDirectory(work);
                localZip = Path.Combine(work, "payload.zip");
                localJson = Path.Combine(work, "release.json");
                await using (var source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var target = new FileStream(localZip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(target, token);
                var originalDigest = metadata.PackageSha256;
                await SidecarPackageValidator.GenerateAsync(localZip, localJson, metadata, token);
                if (!string.Equals(originalDigest, metadata.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Source ZIP changed while preparing upload work.");
                await SidecarPackageValidator.ValidateArchiveAsync(localZip, metadata, token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                return new(false, false, uploadId, profile.RemoteIncoming + "/" + uploadId, work, "prepare", [],
                    "전송 미실행; 생성된 로컬 작업 사본은 보존합니다.", "ZIP 크기와 작업 루트의 여유 공간(64 MB 예비 포함)·쓰기 권한·원본 변경 여부를 확인한 뒤 새 ID로 다시 시도하세요.",
                    FailureKind: error is InvalidDataException ? "integrity-failed" : "local-preparation-failed", WorkPreserved: work is not null && Directory.Exists(work));
            }
        }
        var remote = profile.RemoteIncoming + "/" + uploadId;
        var steps = BuildSteps(profile, remote, localZip, localJson);
        for (var i = 0; !dryRun && i < steps.Count; i++)
        {
            int[] numbers = [2, 3, 4, 5, 5, 6];
            progress?.Invoke(new(numbers[i], UploadPresentation.Stage(steps[i].Name) + " 중", i == 1 ? metadata.PackageSize : 0));
            int exit;
            string? kind = null;
            try { exit = await runner.RunAsync(steps[i].Executable, steps[i].Arguments, token); }
            catch (UploadExecutableMissingException) { exit = -1; kind = "executable-missing"; }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { exit = -1; }
            if (exit != 0)
                return new(false, false, uploadId, remote, work, steps[i].Name, steps,
                    RemainingAt(i), UploadFailureGuidance.For(steps[i].Name, exit, profile, kind), exit, kind, WorkPreserved: work is not null);
        }
        string? cleanupWarning = null;
        if (!dryRun && !keepWork)
        {
            try { UploadWork.DeleteOwned(workRoot, work!); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            { cleanupWarning = "원격 업로드는 완료됐지만 로컬 사본 정리가 완료되지 않았습니다. 작업 폴더를 확인하세요. 재전송은 필요하지 않습니다."; }
        }
        return new(true, dryRun, uploadId, remote, work, null, steps,
            dryRun ? "변경 없음: payload.zip, release.json 생성 예정" : "payload.zip과 release.json 최종 파일 확인 완료; .uploading 없음",
            "운영자가 서버 콘솔에서 검사 결과를 확인하고 승인합니다. 승격은 검증 후 별도 수행합니다.",
            WorkPreserved: !dryRun && (keepWork || cleanupWarning is not null), WorkCleanupWarning: cleanupWarning);
    }

    private static string RemainingAt(int stage) => stage switch
    {
        0 => "mkdir 실패: 폴더 존재 여부 불명; 기존 자료를 변경하지 않음",
        1 => "payload.zip.uploading 일부 또는 전부가 남을 수 있음; 이름 변경 미실행",
        2 => "payload.zip.uploading 및 release.json.uploading 일부 또는 전부가 남을 수 있음; 이름 변경 미실행",
        3 => "ZIP 이름 변경 결과 불명; JSON은 .uploading 상태; 후속 단계 미실행",
        4 => "ZIP은 최종 이름; JSON 이름 변경 결과 불명; 후속 단계 미실행",
        _ => "두 최종 파일 또는 .uploading 잔여 여부 확인 실패; 운영자 직접 점검 필요"
    };

    private static List<UploadStep> BuildSteps(UploadProfile profile, string remote, string zip, string json)
    {
        UploadStep Ssh(string name, string command) => UploadTransport.Ssh(profile, name, command);
        UploadStep Scp(string name, string local, string file) => new(name, "scp", [.. UploadTransport.Options(profile, true), Path.GetFullPath(local), profile.Server + ":" + remote + "/" + file]);
        return [
            Ssh("mkdir", "mkdir -- " + remote),
            Scp("zip-transfer", zip, "payload.zip.uploading"),
            Scp("json-transfer", json, "release.json.uploading"),
            Ssh("zip-rename", $"mv -n -- {remote}/payload.zip.uploading {remote}/payload.zip && test ! -e {remote}/payload.zip.uploading && test -f {remote}/payload.zip"),
            Ssh("json-rename", $"mv -n -- {remote}/release.json.uploading {remote}/release.json && test ! -e {remote}/release.json.uploading && test -f {remote}/release.json"),
            Ssh("verify", $"test -f {remote}/payload.zip && test -f {remote}/release.json && test ! -e {remote}/payload.zip.uploading && test ! -e {remote}/release.json.uploading")
        ];
    }
}
