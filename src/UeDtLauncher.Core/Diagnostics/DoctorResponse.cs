namespace UeDtLauncher;

/// <summary>Keep transport evidence intact through client-side composition.</summary>
internal static class DoctorResponse
{
    internal static DoctorReport ValidateEnvelope(ManagedAgentResponse response, ManagedAgentRequest request)
    {
        if (response.ProtocolVersion != request.ProtocolVersion || response.CorrelationId != request.CorrelationId || response.DoctorReport is null)
            throw Invalid(request.CorrelationId);
        if (response.Success != response.DoctorReport.Healthy) throw Invalid(request.CorrelationId);
        return Validate(response.DoctorReport, request.DoctorTarget, request.CorrelationId);
    }

    internal static DoctorReport Validate(DoctorReport report, DoctorTarget? expected, string? correlationId = null)
    {
        var id = correlationId ?? report.SupportId ?? Guid.NewGuid().ToString("N");
        if (report.Checks is null || report.Checks.Any(c => c is null)) throw Invalid(id);
        if (expected is not null && report.Target is not null && report.Target != expected) throw Invalid(id);
        if(report.SelectedRelease is { } release && expected is not null)
        {
            release.Validate();
            if(release.ProjectId!=expected.ProjectId || release.Environment!=expected.Environment || release.Channel!=expected.Channel || release.Platform!=expected.Platform ||
               expected.VersionPolicy=="exact" && release.Version!=expected.RequestedVersion)throw Invalid(id);
        }
        var incomplete = report.Checks.Count == 0 || (expected is not null && report.Target is null) ||
            report.PreparationState is not ("checks-passed" or "verification-pending" or "action-required");
        var checks = new List<DoctorCheck>();
        foreach (var check in report.Checks)
        {
            if (check.State is not ("passed" or "failed" or "waiting" or "deferred" or "not-applicable"))
            {
                incomplete = true;
                // Legacy boolean failures remain failures; an unrecognised success is unverified.
                checks.Add(check with { State = check.Success ? "deferred" : "failed", Success = false });
                continue;
            }
            if (check.Success != (check.State is "passed" or "not-applicable")) throw Invalid(id);
            checks.Add(check);
        }
        var failed = checks.Any(c => c.State == "failed");
        var actionable = checks.Any(c => c.State is "failed" or "waiting");
        if (report.Healthy && failed) throw Invalid(id);
        if (!report.Healthy && !failed)
        {
            if (!incomplete) throw Invalid(id);
            checks.Add(new("agent-diagnostic-failed", false, "업데이트 서비스 진단을 완료하지 못했습니다.")
            { State = "failed", Code = "diagnostic-response-invalid", Subject = "agent", ActionOwner = "admin" });
            actionable = true;
        }
        if (report.PreparationState == "checks-passed" && !incomplete &&
            checks.Any(c => c.State is "failed" or "waiting" or "deferred")) throw Invalid(id);
        if (report.PreparationState == "action-required" && !actionable && !incomplete) throw Invalid(id);
        if (incomplete || (report.PreparationState == "verification-pending" && checks.All(c => c.State is "passed" or "not-applicable")))
            if (!checks.Any(c => c.Name == "agent-evidence-incomplete"))
                checks.Add(new("agent-evidence-incomplete", false, "일부 진단 근거를 확인할 수 없습니다.")
                { State = "deferred", Code = "diagnostic-evidence-incomplete", Subject = "agent", ActionOwner = "admin",
                    NextAction = "업데이트 서비스를 갱신하거나 진단을 다시 실행해 주세요." });
        return DoctorPresentation.Complete(report with { Checks = checks, SupportId = report.SupportId ?? id });
    }

    internal static DoctorReport Merge(DoctorReport agentReport, DoctorTarget expected, IReadOnlyList<DoctorCheck> clientChecks)
    {
        var verified = Validate(agentReport, expected);
        // Never fill a missing Agent target with the target merely requested by this client.
        return DoctorPresentation.Complete(verified with { Checks = clientChecks.Concat(verified.Checks).ToArray() });
    }

    private static AgentOperationException Invalid(string id) => new("diagnostic-response-invalid", id,
        "업데이트 서비스의 진단 응답을 확인할 수 없습니다. 서비스를 점검한 뒤 다시 시도해 주세요.");
}
