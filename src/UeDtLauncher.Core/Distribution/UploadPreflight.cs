namespace UeDtLauncher;

public sealed class UploadExecutableMissingException : IOException;
public sealed record UploadCheckItem(string Name, string State, string NextAction);
public sealed record UploadCheckResult(bool Success, IReadOnlyList<UploadCheckItem> Checks);

public static class UploadFailureGuidance
{
    public const string MissingExecutable = "Windows 선택 기능 'OpenSSH 클라이언트'를 설치하거나 PATH를 확인하세요. Linux는 OpenSSH 클라이언트 설치를 확인하세요.";
    public static string For(string stage, int exitCode, UploadProfile profile, string? kind = null)
    {
        if (kind == "executable-missing") return MissingExecutable;
        if (exitCode == 255)
            return $"서버 주소·포트·키 인증 설정·호스트 키 등록을 확인하세요. 최초 1회 터미널에서 ssh -p {profile.Port ?? 22} {profile.Server} 로 접속해 호스트 키를 확인·등록하세요. upload/check는 키 인증만 지원하며 비밀번호를 묻지 않습니다.";
        return stage switch
        {
            "mkdir" => "같은 업로드 ID 폴더가 이미 있거나 incoming 쓰기 권한이 없을 수 있습니다. 폴더를 재사용하지 말고 권한 확인 후 새 ID로 다시 시도하세요.",
            "zip-transfer" or "json-transfer" => "네트워크 중단·원격 디스크 부족·쓰기 권한을 확인하세요. 같은 폴더를 재사용하지 말고 새 ID로 다시 시도하세요.",
            "connection" => "OpenSSH 실행 권한·서버 주소·포트·키 인증 설정을 확인하세요. 원격 출력은 보관하지 않습니다.",
            "incoming-directory" => "설정한 remoteIncoming이 서버의 디렉터리인지 확인하세요. 점검 명령은 폴더를 만들지 않습니다.",
            "incoming-writable" => "업로드 계정의 incoming 쓰기 권한을 관리자에게 확인 요청하세요. 점검은 쓰기 가능 여부만 조회합니다.",
            _ => "OpenSSH 실행과 원격 파일 상태·권한을 운영자에게 확인 요청하세요. 남은 자료를 확인하고 같은 업로드 폴더를 재사용하지 마세요."
        };
    }
}

internal static class UploadTransport
{
    internal static List<string> Options(UploadProfile profile, bool scp)
    {
        var args = new List<string> { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UpdateHostKeys=no",
            "-o", "PreferredAuthentications=publickey", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
            "-o", "PermitLocalCommand=no", "-o", "ClearAllForwardings=yes", "-o", "ControlMaster=no", "-o", "ControlPath=none",
            "-o", "ConnectTimeout=10", "-o", "ConnectionAttempts=1", scp ? "-P" : "-p", (profile.Port ?? 22).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (profile.IdentityFile is not null) { args.Add("-i"); args.Add(Path.GetFullPath(profile.IdentityFile)); }
        return args;
    }
    internal static UploadStep Ssh(UploadProfile profile, string name, string command) => new(name, "ssh", [.. Options(profile, false), "-T", profile.Server!, command]);
}

public sealed class UploadPreflight(IUploadProcessRunner runner)
{
    public async Task<UploadCheckResult> CheckAsync(UploadProfile profile, CancellationToken token = default)
    {
        profile.Validate();
        var checks = new List<UploadCheckItem>();
        foreach (var executable in new[] { "ssh", "scp" })
        {
            var available = runner.IsAvailable(executable);
            checks.Add(new(executable, available ? "passed" : "failed", available ? "실행 파일 확인" : UploadFailureGuidance.MissingExecutable));
        }
        var proceed = checks.All(c => c.State == "passed");
        foreach (var step in new[] { UploadTransport.Ssh(profile, "connection", "true"),
                     UploadTransport.Ssh(profile, "incoming-directory", "test -d " + profile.RemoteIncoming),
                     UploadTransport.Ssh(profile, "incoming-writable", "test -w " + profile.RemoteIncoming) })
        {
            if (!proceed) { checks.Add(new(step.Name, "not-run", "앞 단계 실패를 먼저 해결하세요.")); continue; }
            int exit;
            string? kind = null;
            try { exit = await runner.RunAsync(step.Executable, step.Arguments, token); }
            catch (UploadExecutableMissingException) { exit = -1; kind = "executable-missing"; }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { exit = -1; }
            proceed = exit == 0;
            checks.Add(new(step.Name, proceed ? "passed" : "failed", proceed ? "조회 통과 (원격 파일·폴더 생성 없음)" : UploadFailureGuidance.For(step.Name, exit, profile, kind)));
        }
        return new(checks.All(c => c.State == "passed"), checks);
    }

    public static void Write(TextWriter output, UploadCheckResult result)
    {
        output.WriteLine("업로드 사전 점검 — 키 인증 전용, 원격 변경 없음");
        foreach (var check in result.Checks)
        {
            var name = check.Name switch { "connection" => "접속·키 인증", "incoming-directory" => "incoming 디렉터리", "incoming-writable" => "incoming 쓰기 권한", _ => check.Name + " 실행 파일" };
            var state = check.State switch { "passed" => "통과", "failed" => "실패", _ => "미실행" };
            output.WriteLine($"{name}: {state} — {HumanText.Safe(check.NextAction)}");
        }
        output.WriteLine(result.Success ? "다음 할 일: ZIP을 준비한 뒤 upload --dry-run으로 계획을 확인하세요." : "다음 할 일: 실패 항목을 해결한 뒤 upload check를 다시 실행하세요.");
    }
}
