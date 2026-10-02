using System.Text;

namespace UeDtLauncher;

public sealed record DoctorTarget(string DeploymentMode, string? ProjectId, string Environment, string Channel,
    string Platform, string VersionPolicy, string? RequestedVersion)
{
    public static DoctorTarget From(LauncherConfig config) => new(config.DeploymentMode, config.ProjectId,
        config.Environment, config.Channel, config.TargetPlatform, config.VersionPolicy, config.RequestedVersion);

    public void Apply(LauncherConfig config)
    {
        if (DeploymentMode != config.DeploymentMode || Platform != config.TargetPlatform || VersionPolicy is not ("latest" or "exact"))
            throw new ArgumentException("Invalid diagnostic mode, platform or version policy.");
        // The version is a validation placeholder for latest, never an execution target.
        new ReleaseSelection(ProjectId ?? "", Environment, Channel, Platform,
            VersionPolicy == "exact" ? RequestedVersion ?? "" : "diagnostic").Validate();
        if (VersionPolicy == "latest" && RequestedVersion is not null) throw new ArgumentException("Latest diagnostics cannot request an exact version.");
        config.ProjectId = ProjectId; config.Environment = Environment; config.Channel = Channel;
        config.VersionPolicy = VersionPolicy; config.RequestedVersion = RequestedVersion;
    }
}

public static class DoctorPresentation
{
    public const string Capability = "read-only-doctor-v1";
    public static DoctorCheck Normalize(DoctorCheck check, string subject)
    {
        var state = check.State ?? (check.Name == "runtime-data-host-preflight" ? "deferred" : check.Success ? "passed" : "failed");
        var code = check.Code ?? (check.Name == "config" && !check.Success ? "configuration-invalid" : check.Name);
        return check with { State = state, Code = code, Subject = check.Subject ?? subject,
            ActionOwner = check.ActionOwner ?? (state is "failed" or "waiting" ? "admin" : state == "deferred" ? "user" : null),
            NextAction = check.NextAction ?? (state == "failed" ? "지원 ID와 검사 코드를 관리자에게 전달해 주세요." :
                state == "deferred" ? "실행 직전 사용자 환경 검사가 추가로 필요합니다." : null) };
    }

    public static DoctorReport Complete(DoctorReport report) => report with
    {
        PreparationState = report.Checks.Any(c => c.State is "failed" or "waiting") ? "action-required" :
            report.Checks.Any(c => c.State is null or "deferred") ? "verification-pending" : "checks-passed",
        SupportId = report.SupportId ?? Guid.NewGuid().ToString("N")
    };

    public static string Summary(DoctorReport report) => report.PreparationState switch
    { "checks-passed" => "점검 완료", "action-required" => "조치 필요", _ => "추가 검증 필요" };

    public static string FormatText(DoctorReport report)
    {
        var text = new StringBuilder(Summary(report)).AppendLine(" · 설치/실행 가능 보장은 아닙니다.");
        foreach (var check in report.Checks)
            text.Append('[').Append(check.State ?? "미검증").Append("] ").Append(check.Code ?? check.Name).Append(": ")
                .AppendLine(check.Message).AppendLine(check.NextAction is null ? "" : $"  {check.ActionOwner}: {check.NextAction}");
        text.Append("지원 ID: ").Append(report.SupportId ?? "미지원");
        return DiagnosticRedactor.Redact(text.ToString());
    }
}
