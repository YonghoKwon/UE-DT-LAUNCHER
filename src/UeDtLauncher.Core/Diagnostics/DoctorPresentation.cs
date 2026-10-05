using System.Text;

namespace UeDtLauncher;

public sealed record DoctorTarget(string DeploymentMode, string? ProjectId, string Environment, string Channel,
    string Platform, string VersionPolicy, string? RequestedVersion, string ClientProfile = "general")
{
    public static DoctorTarget From(LauncherConfig config) => new(config.DeploymentMode, config.ProjectId,
        config.Environment, config.Channel, config.TargetPlatform, config.VersionPolicy, config.RequestedVersion, config.ClientProfile);

    public void Apply(LauncherConfig config)
    {
        if (DeploymentMode != config.DeploymentMode || Platform != config.TargetPlatform || VersionPolicy is not ("latest" or "exact") || ClientProfile is not ("general" or "developer"))
            throw new ArgumentException("Invalid diagnostic mode, platform or version policy.");
        // The version is a validation placeholder for latest, never an execution target.
        new ReleaseSelection(ProjectId ?? "", Environment, Channel, Platform,
            VersionPolicy == "exact" ? RequestedVersion ?? "" : "diagnostic").Validate();
        if (VersionPolicy == "latest" && RequestedVersion is not null) throw new ArgumentException("Latest diagnostics cannot request an exact version.");
        if (!config.IsManagedClientContext && string.IsNullOrWhiteSpace(config.DistributionServerUrl) && ProjectId != config.ProjectId && !config.Projects.Any(p => p.ProjectId == ProjectId))
            throw new UnauthorizedAccessException("Diagnostic project is not declared in managed settings.");
        config.ProjectId = ProjectId; config.Environment = Environment; config.Channel = Channel;
        config.VersionPolicy = VersionPolicy; config.RequestedVersion = RequestedVersion; config.ClientProfile = ClientProfile;
    }
}

public static class DoctorPresentation
{
    public const string Capability = "read-only-doctor-v1";
    public static DoctorCheck Failure(string name, Exception error, string subject)
    {
        var code = LauncherFailure.Code(error);
        if (name == "agent" && code is "storage-failed" or "unknown") code = "service-unavailable";
        var guidance = LauncherGuidance.For(code);
        return new(name, false, guidance.Message) { State = "failed", Code = code, Subject = subject,
            ActionOwner = guidance.Owner, NextAction = guidance.NextAction };
    }

    public static DoctorCheck ReleaseReadiness(LauncherConfig config, DistributionCatalog catalog)
    {
        var candidates = catalog.Projects.Where(p => p.ProjectId.Equals(config.ProjectId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Releases).Where(r => r.Environment == config.Environment && r.Channel == config.Channel && r.Platform == config.TargetPlatform && r.AllowedClientProfiles.Contains(config.ClientProfile)).ToArray();
        var selected = config.VersionPolicy == "exact" ? candidates.FirstOrDefault(r => r.Version == config.RequestedVersion) :
            CatalogRecommendation.Find(catalog.SelectionPolicy, candidates, r => r.IsLatest);
        if (selected is not null) return new("authorized-releases", true, $"선택 배포 {selected.Version}의 조회가 가능합니다. 설치 파일과 실행 환경은 작업 시 추가 검증합니다.")
        { State = "passed", Code = "release-available" };
        var code = candidates.Length > 0 && config.VersionPolicy == "latest" ? "no-promoted-release" : "no-authorized-release";
        var guidance = LauncherGuidance.For(code);
        return new("authorized-releases", false, guidance.Message) { State = "waiting", Code = code, ActionOwner = guidance.Owner, NextAction = guidance.NextAction };
    }
    public static DoctorCheck Normalize(DoctorCheck check, string subject)
    {
        var state = check.State ?? (check.Name == "runtime-data-host-preflight" ? "deferred" : check.Success ? "passed" : "failed");
        var code = check.Code ?? (check.Name == "config" && !check.Success ? "configuration-invalid" : check.Name);
        var guidance = LauncherGuidance.For(code);
        return check with { State = state, Code = code, Subject = check.Subject ?? subject,
            ActionOwner = check.ActionOwner ?? (state is "failed" or "waiting" ? "admin" : state == "deferred" ? "user" : null),
            NextAction = check.NextAction ?? (state == "failed" ? guidance.NextAction :
                state == "deferred" ? "실행 직전 사용자 환경 검사가 추가로 필요합니다." : null) };
    }

    public static DoctorReport Complete(DoctorReport report) => report with
    {
        PreparationState = report.Checks.Any(c => c.State is "failed" or "waiting") ? "action-required" :
            report.Checks.Count == 0 || report.Checks.Any(c => c.State is not ("passed" or "not-applicable")) ? "verification-pending" : "checks-passed",
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
