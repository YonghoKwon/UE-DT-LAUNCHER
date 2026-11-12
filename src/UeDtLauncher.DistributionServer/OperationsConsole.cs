using System.Text.Json;

namespace UeDtLauncher.Distribution;

public sealed record ConsoleJobDetail(string Id, string State, string? Phase, long ProcessedBytes, long TotalBytes,
    string? ProjectId, string? Version, string? Platform, long? PackageSize, string? SnapshotDigest, string? Error);
public sealed record ConsoleCleanupChoice(string Key, string? JobId, string? Temporary, long Bytes);

/// <summary>Terminal-only guided operations. Leases cover individual reads/mutations, never the session or prompts.</summary>
public sealed class OperationsConsole(DistributionSettings settings, string configPath, TextReader input, TextWriter output, bool interactive)
{
    public async Task RunAsync(CancellationToken token = default)
    {
        if (!interactive)
        {
            output.WriteLine("안내형 콘솔은 터미널 입력만 지원합니다. 조회는 list / inspect / usage / token-list / client-key list 명령을 사용하세요. 변경 없음.");
            return;
        }
        while (!token.IsCancellationRequested)
        {
            output.WriteLine("① 접수 현황 ② 작업 상세 ③ 승인 ④ 거절 ⑤ 실패 재시도 ⑥ 승격 ⑦ 저장 공간·정리 ⑧ 자격 현황 ⓪ 종료");
            var action = await input.ReadLineAsync(token);
            if (action is null or "0") return;
            try
            {
                switch (action)
                {
                    case "1": await ListAsync(); break;
                    case "2":
                        if (await PromptAsync("작업 ID", token) is { } detailId) Print(await SharedAsync(store => DetailAsync(store, detailId)));
                        else return;
                        break;
                    case "3": case "4": case "5": await MutateJobAsync(action, token); break;
                    case "6": await PromoteAsync(token); break;
                    case "7": await CleanupAsync(token); break;
                    case "8": await CredentialsAsync(); break;
                    default: output.WriteLine("잘못된 입력입니다. 변경 없이 종료합니다."); return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { CliErrors.Write(output, error, server: true); }
        }
    }

    private async Task<T> SharedAsync<T>(Func<IntakeStore, Task<T>> action)
    {
        var store = new IntakeStore(settings);
        using var lease = DistributionMaintenanceLease.Acquire(settings.Root, false);
        return await action(store);
    }

    private Task<string?> PromptAsync(string prompt, CancellationToken token)
    {
        output.WriteLine(prompt + ":");
        return input.ReadLineAsync(token).AsTask();
    }

    private async Task<bool> ConfirmAsync(string expected, CancellationToken token)
    {
        var answer = await PromptAsync("실행 대상 확인: " + expected + " 값을 정확히 다시 입력 (다르면 취소)", token);
        if (answer == expected) return true;
        output.WriteLine("확인 불일치 또는 EOF: 변경하지 않았습니다.");
        return false;
    }

    private void Print(object value) => output.WriteLine(DiagnosticRedactor.Redact(JsonSerializer.Serialize(value, JsonFiles.Options)));

    private static async Task<ConsoleJobDetail> DetailAsync(IntakeStore store, string id)
    {
        var job = store.Get(id);
        ReleaseSidecar? metadata = null;
        string? digest = null;
        if (job.Snapshot is not null)
        {
            var processing = Path.Combine(store.Root, "processing");
            var snapshot = SafePath.ResolveInsideChecked(processing, Path.GetRelativePath(processing, job.Snapshot));
            var document = SafePath.ResolveInsideChecked(snapshot, "release.json");
            var bytes = await SidecarPackageValidator.ReadDocumentAsync(document);
            metadata = SidecarPackageValidator.Parse(bytes);
            digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        return new(job.Id, job.State, job.Phase, job.ProcessedBytes, job.TotalBytes, metadata?.ProjectId,
            metadata?.Version, metadata?.Platform, metadata?.PackageSize, digest, DiagnosticRedactor.RedactNullable(job.Message));
    }

    private async Task ListAsync()
    {
        output.WriteLine("대기 waiting / 검사 중 validating / 승인 대기 pending / 게시 중 publishing / 실패 failed / 거절 rejected / 게시됨 published; snapshot 없는 프로젝트·버전·플랫폼은 미검증입니다.");
        var rows = await SharedAsync(async store =>
        {
            var result = new List<ConsoleJobDetail>();
            foreach (var job in store.List().OrderBy(j => j.State).ThenBy(j => j.Id))
            {
                try { result.Add(await DetailAsync(store, job.Id)); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    result.Add(new(job.Id, job.State, job.Phase, job.ProcessedBytes, job.TotalBytes,
                        null, null, null, null, null, "비공개 snapshot 요약을 확인할 수 없습니다. 작업 상세와 저장 상태를 점검하세요."));
                }
            }
            return result;
        });
        Print(rows);
    }

    private async Task MutateJobAsync(string action, CancellationToken token)
    {
        var id = await PromptAsync("작업 ID", token);
        if (id is null) return;
        var before = await SharedAsync(store => DetailAsync(store, id));
        output.WriteLine(action switch { "3" => "승인: 게시만 수행하며 승격하지 않습니다.", "4" => "선택한 승인 대기 작업을 거절합니다.", _ => "실패 작업을 대기 상태로 되돌립니다. watch의 다음 검사를 확인하세요." });
        Print(before);
        if (!await ConfirmAsync(id, token)) return;
        var result = await SharedAsync(async store =>
        {
            if (await DetailAsync(store, id) != before) throw new InvalidOperationException("Job preview changed; inspect and confirm again.");
            if (action == "3") await new ApprovedPublisher(store).ApproveAsync(id, token);
            else if (action == "4") store.Reject(id);
            else store.Retry(id);
            return store.Get(id);
        });
        Print(new { Target = id, Result = result.State });
    }

    private async Task PromoteAsync(CancellationToken token)
    {
        var releases = await SharedAsync(store => Task.FromResult(new ApprovedPublisher(store).List()));
        Print(releases.Select(r => new { r.ReleaseId, r.Metadata.ProjectId, r.Metadata.Version, r.Metadata.Platform }));
        var releaseId = await PromptAsync("게시된 ReleaseId 전체 선택", token);
        if (releaseId is null) return;
        var selected = releases.SingleOrDefault(r => r.ReleaseId == releaseId) ?? throw new ArgumentException("Select a published release.");
        var meta = selected.Metadata;
        var selection = new ReleaseSelection(meta.ProjectId, meta.Environment, meta.Channel, meta.Platform, meta.Version);
        var inspection = await SharedAsync(store => Task.FromResult(new ReleasePromotions(store).Inspect(selection)));
        Print(new { Target = releaseId, Current = inspection.RecommendedRelease, inspection.Revision, inspection.Track });
        var reason = await PromptAsync("검증 완료 사유", token);
        if (reason is null || !await ConfirmAsync(meta.Version, token)) return;
        var result = await SharedAsync(store => Task.FromResult(new ReleasePromotions(store).Promote(selection, inspection.Revision, reason)));
        Print(result);
    }

    public static async Task<IReadOnlyList<ConsoleCleanupChoice>> CleanupChoicesAsync(IntakeStore store)
    {
        var choices = new List<ConsoleCleanupChoice>();
        async Task Add(string key, string? job, string? temporary)
        {
            try
            {
                var plan = await RetentionMaintenance.PlanAsync(store.Settings, job is null ? [] : [job], temporary is null ? [] : [temporary]);
                if (plan.Candidates.Count > 0) choices.Add(new(key, job, temporary, plan.Candidates.Sum(c => c.Bytes)));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        foreach (var job in store.List().Where(j => j.State is "failed" or "rejected")) await Add("job:" + job.Id, job.Id, null);
        var legacy = JsonSerializer.SerializeToElement(StorageMaintenance.Cleanup(store, false), JsonFiles.Options);
        foreach (var directory in legacy.GetProperty("directories").EnumerateArray())
        {
            var relative = Path.GetRelativePath(store.Root, directory.GetString()!).Replace('\\', '/');
            await Add("temp:" + relative, null, relative);
        }
        return choices;
    }

    private async Task CleanupAsync(CancellationToken token)
    {
        var choices = await SharedAsync(async store =>
        {
            Print(StorageMaintenance.Usage(store));
            Print(RetentionMaintenance.Inspect(settings));
            return await CleanupChoicesAsync(store);
        });
        Print(choices);
        var key = await PromptAsync("정리할 key 하나 (공개·승격·pending·active는 제외)", token);
        if (key is null) return;
        var choice = choices.SingleOrDefault(c => c.Key == key) ?? throw new ArgumentException("Select an eligible cleanup candidate.");
        var plan = await SharedAsync(_ => RetentionMaintenance.PlanAsync(settings,
            choice.JobId is null ? [] : [choice.JobId], choice.Temporary is null ? [] : [choice.Temporary]));
        Print(plan);
        if (!await ConfirmAsync(plan.PlanId, token)) return;
        var path = Path.Combine(settings.Root, "console-retention-" + plan.PlanId + ".json");
        await SharedAsync(async _ => { await JsonFiles.WriteAsync(path, plan, false, token); return true; });
        // No shared lease survives into ApplyAsync; its existing offline locks and reference/hash checks remain authoritative.
        try { Print(await RetentionMaintenance.ApplyAsync(settings, plan, true)); }
        catch (IOException)
        {
            output.WriteLine("적용 보류: serve/watch 또는 다른 유지보수 작업이 실행 중이거나 저장 I/O를 확인할 수 없습니다. 프로세스를 종료하지 않습니다. 상태 확인 뒤 다음 명령을 직접 실행하세요:");
            output.WriteLine("UeDtLauncher.DistributionServer retention apply --config " + Quote(configPath) + " --plan " + Quote(path) + " --confirm");
        }
    }

    private static string Quote(string text) => "'" + text.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\\''") + "'";

    private async Task CredentialsAsync()
    {
        await SharedAsync(store =>
        {
            Print(new DistributionTokens(store).List().Select(c => new { c.Id, c.Client, c.ExpiresAtUtc, c.Revoked, c.Expired }));
            Print(new DistributionDeviceKeys(store).List().Select(c => new { c.KeyId, c.ClientId, c.ExpiresAtUtc, c.Revoked, c.Expired }));
            return Task.FromResult(true);
        });
    }
}
