using System.Text.Json;

namespace UeDtLauncher.Distribution;

public sealed record ConsoleJobDetail(string Id, string State, string? Phase, long ProcessedBytes, long TotalBytes,
    string? ProjectId, string? Version, string? Platform, long? PackageSize, string? SnapshotDigest, string? Error,
    string? Environment = null, string? Channel = null, string? EntryPoint = null);
public sealed record ConsoleCleanupChoice(string Key, string? JobId, string? Temporary, long Bytes);

/// <summary>Terminal-only guided operations. Leases cover individual reads/mutations, never the session or prompts.</summary>
public sealed class OperationsConsole(DistributionSettings settings, string configPath, TextReader input, TextWriter output, bool interactive)
{
    private bool inputEnded;

    public async Task RunAsync(CancellationToken token = default)
    {
        if (!interactive)
        {
            output.WriteLine("안내형 콘솔은 터미널 입력만 지원합니다. 조회는 list / inspect / usage / token-list / client-key list 명령을 사용하세요. 변경 없음.");
            return;
        }
        while (!token.IsCancellationRequested && !inputEnded)
        {
            OperationsConsoleTables.Summary(output, await SharedAsync(store => Task.FromResult(store.List())));
            output.WriteLine("1 접수 현황 | 2 작업 상세 | 3 승인 | 4 거절 | 5 실패 재시도 | 6 승격 | 7 저장 공간·정리 | 8 자격 현황 | 0 종료");
            var action = await input.ReadLineAsync(token);
            if (action is null or "0") return;
            try
            {
                switch (action)
                {
                    case "1": await ListAsync(); break;
                    case "2": await SelectJobAsync("2", token); break;
                    case "3": case "4": case "5": await MutateJobAsync(action, token); break;
                    case "6": await PromoteAsync(token); break;
                    case "7": await CleanupAsync(token); break;
                    case "8": await CredentialsAsync(); break;
                    default: output.WriteLine("잘못된 메뉴 번호입니다. 0~8에서 다시 선택하세요. 변경 없음."); break;
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

    private async Task<string?> PromptAsync(string prompt, CancellationToken token)
    {
        output.WriteLine(prompt + ":");
        var answer = await input.ReadLineAsync(token);
        if (answer is null) inputEnded = true;
        return answer;
    }

    private async Task<bool> ConfirmAsync(string expected, CancellationToken token)
    {
        var answer = await PromptAsync("확인하려면 " + expected + " 을 입력하세요 (다르면 취소)", token);
        if (answer == expected) return true;
        output.WriteLine("확인 불일치 또는 EOF: 변경하지 않았습니다.");
        return false;
    }

    private static async Task<ConsoleJobDetail> DetailAsync(IntakeStore store, string id)
    {
        var job = store.Get(id);
        ReleaseSidecar? metadata = null;
        string? digest = null;
        var message = DiagnosticRedactor.RedactNullable(job.Message);
        if (job.Snapshot is not null)
        {
            try
            {
            var processing = Path.Combine(store.Root, "processing");
            var snapshot = SafePath.ResolveInsideChecked(processing, Path.GetRelativePath(processing, job.Snapshot));
            var document = SafePath.ResolveInsideChecked(snapshot, "release.json");
            var bytes = await SidecarPackageValidator.ReadDocumentAsync(document);
            digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            metadata = SidecarPackageValidator.Parse(bytes);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            { message = "비공개 snapshot 요약을 확인할 수 없습니다. " + (message ?? "2번 상세와 서버 저장 상태를 점검하세요."); }
        }
        return new(job.Id, job.State, job.Phase, job.ProcessedBytes, job.TotalBytes, metadata?.ProjectId,
            metadata?.Version, metadata?.Platform, metadata?.PackageSize, digest, message, metadata?.Environment, metadata?.Channel, metadata?.EntryPoint);
    }

    private Task<List<ConsoleJobDetail>> JobRowsAsync() => SharedAsync(async store =>
    {
        var rows = new List<ConsoleJobDetail>();
        foreach (var job in store.List().OrderBy(j => j.State).ThenBy(j => j.Id)) rows.Add(await DetailAsync(store, job.Id));
        return rows;
    });

    private async Task ListAsync() => OperationsConsoleTables.Jobs(output, await JobRowsAsync());

    private static T? Select<T>(string answer, IReadOnlyList<T> rows, Func<T, string> id) where T : class
    {
        if (int.TryParse(answer, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= rows.Count)
            return rows[number - 1];
        return rows.SingleOrDefault(row => id(row) == answer);
    }

    private async Task<ConsoleJobDetail?> SelectJobAsync(string action, CancellationToken token)
    {
        var rows = (await JobRowsAsync()).Where(row => action switch
        {
            "3" => row.State is "pending" or "publishing", "4" => row.State == "pending", "5" => row.State == "failed", _ => true
        }).ToArray();
        OperationsConsoleTables.Jobs(output, rows);
        if (rows.Length == 0) return null;
        var answer = await PromptAsync("표시한 번호 또는 작업 ID 전체 선택", token);
        if (answer is null) return null;
        var selected = Select(answer, rows, row => row.Id) ?? throw new ArgumentException("Select a displayed job.");
        var current = await SharedAsync(store => DetailAsync(store, selected.Id));
        if (current != selected) { output.WriteLine("목록이 변경됐습니다. 다시 조회하고 대상을 선택하세요. 변경 없음."); return null; }
        OperationsConsoleTables.Detail(output, current);
        return current;
    }

    private async Task MutateJobAsync(string action, CancellationToken token)
    {
        var before = await SelectJobAsync(action, token);
        if (before is null) return;
        if (action == "3" && before.Version is null) { output.WriteLine("승인할 비공개 snapshot의 버전을 확인할 수 없습니다. 저장 상태를 먼저 점검하세요."); return; }
        var id = before.Id;
        output.WriteLine(action switch { "3" => "승인: 게시만 수행하며 승격하지 않습니다.", "4" => "선택한 작업을 거절합니다.", _ => "실패 작업을 대기 상태로 되돌립니다." });
        if (!await ConfirmAsync(action == "3" ? before.Version! : OperationsConsoleTables.ShortId(id), token)) return;
        var result = await SharedAsync(async store =>
        {
            if (await DetailAsync(store, id) != before) throw new InvalidOperationException("Job preview changed; inspect and confirm again.");
            if (action == "3") await new ApprovedPublisher(store).ApproveAsync(id, token);
            else if (action == "4") store.Reject(id);
            else store.Retry(id);
            return store.Get(id);
        });
        output.WriteLine("결과: " + OperationsConsoleTables.State(result.State) + " · 작업 " + OperationsConsoleTables.ShortId(id));
        output.WriteLine(action switch
        {
            "3" => "다음 할 일: 개발자가 exact 버전으로 검증한 뒤 6번 승격을 실행하세요.",
            "4" => "다음 할 일: 수정한 자료는 새 업로드 ID로 올리세요.",
            _ => "다음 할 일: 서버의 다음 검사를 확인하세요. 오류가 반복되면 원인을 수정하고 새 업로드 ID로 올리세요."
        });
    }

    private static bool SameRelease(PublishedRelease left, PublishedRelease right) => left.ReleaseId == right.ReleaseId && left.JobId == right.JobId && left.Directory == right.Directory &&
        JsonSerializer.Serialize(left.Metadata, JsonFiles.Options) == JsonSerializer.Serialize(right.Metadata, JsonFiles.Options);

    private async Task PromoteAsync(CancellationToken token)
    {
        var snapshot = await SharedAsync(store => Task.FromResult(new ReleasePromotions(store).Snapshot()));
        var releases = snapshot.Releases.OrderBy(r => r.Metadata.ProjectId).ThenBy(r => r.Metadata.Environment).ThenBy(r => r.Metadata.Channel).ThenBy(r => r.Metadata.Platform).ThenBy(r => r.Metadata.Version, StringComparer.Ordinal).ToArray();
        OperationsConsoleTables.Releases(output, releases, snapshot.Events);
        if (releases.Length == 0) return;
        var answer = await PromptAsync("게시된 번호 또는 ReleaseId 전체 선택", token);
        if (answer is null) return;
        var selected = Select(answer, releases, r => r.ReleaseId) ?? throw new ArgumentException("Select a displayed published release.");
        var meta = selected.Metadata;
        var selection = new ReleaseSelection(meta.ProjectId, meta.Environment, meta.Channel, meta.Platform, meta.Version);
        var track = ReleasePromotions.Track(selection);
        var revision = snapshot.Events.LastOrDefault(e => e.Track == track)?.Revision ?? 0;
        var inspection = await SharedAsync(store =>
        {
            var current = new ApprovedPublisher(store).Find(selected.ReleaseId);
            if (current is null || !SameRelease(current, selected)) throw new InvalidOperationException("Publication changed; inspect again.");
            var state = new ReleasePromotions(store).Inspect(selection);
            if (state.Revision != revision) throw new InvalidOperationException("Displayed promotion changed; inspect again.");
            return Task.FromResult(state);
        });
        output.WriteLine("선택 버전: " + HumanText.Safe(meta.Version));
        output.WriteLine("트랙: " + HumanText.Safe(track));
        output.WriteLine("현재 추천: " + HumanText.Safe(inspection.RecommendedRelease ?? "없음") + " · revision " + inspection.Revision);
        var reason = await PromptAsync("검증 완료 사유", token);
        if (reason is null || !await ConfirmAsync(meta.Version, token)) return;
        await SharedAsync(store =>
        {
            var current = new ApprovedPublisher(store).Find(selected.ReleaseId);
            if (current is null || !SameRelease(current, selected)) throw new InvalidOperationException("Publication changed; inspect again.");
            return Task.FromResult(new ReleasePromotions(store).Promote(selection, inspection.Revision, reason));
        });
        output.WriteLine("승격 완료: " + HumanText.Safe(selected.ReleaseId));
        output.WriteLine("다음 할 일: PC는 다음 상태 확인 때 권한 안에서 이 버전을 추천받습니다. 자동 설치는 아닙니다.");
    }

    public static Task<IReadOnlyList<ConsoleCleanupChoice>> CleanupChoicesAsync(IntakeStore store)
        => Task.FromResult<IReadOnlyList<ConsoleCleanupChoice>>(RetentionMaintenance.InspectCandidates(store.Settings)
            .Select(c => new ConsoleCleanupChoice(c.Key, c.JobId, c.Temporary, c.Bytes)).ToArray());

    private async Task CleanupAsync(CancellationToken token)
    {
        output.WriteLine("정리 후보의 상태·크기를 조회합니다. 파일 내용의 전체 해시는 선택 후 계획 단계에서 계산합니다.");
        var choices = await SharedAsync(async store =>
        {
            var usage = JsonSerializer.SerializeToElement(StorageMaintenance.Usage(store), JsonFiles.Options);
            output.WriteLine("저장 구분 | 크기");
            foreach (var entry in usage.GetProperty("directories").EnumerateArray())
                output.WriteLine(HumanText.Safe(entry.GetProperty("name").GetString()) + " | " + HumanText.Size(entry.GetProperty("bytes").GetInt64()));
            output.WriteLine("공개·승격·pending·active 자료는 정리에서 보호합니다.");
            return await CleanupChoicesAsync(store);
        });
        output.WriteLine("번호 | 크기 | 정리 대상");
        for (var i = 0; i < choices.Count; i++) output.WriteLine($"{i + 1,4} | {HumanText.Size(choices[i].Bytes),9} | {HumanText.Safe(choices[i].Key)}");
        var key = await PromptAsync("표시한 번호 또는 정리 대상 key 하나 선택", token);
        if (key is null) return;
        var choice = Select(key, choices, c => c.Key) ?? throw new ArgumentException("Select an eligible cleanup candidate.");
        var plan = await SharedAsync(_ => RetentionMaintenance.PlanAsync(settings,
            choice.JobId is null ? [] : [choice.JobId], choice.Temporary is null ? [] : [choice.Temporary]));
        if (plan.Candidates.Sum(c => c.Bytes) != choice.Bytes)
        { output.WriteLine("정리 대상 크기가 목록 표시 뒤 변경됐습니다. 다시 조회하고 선택하세요. 변경 없음."); return; }
        output.WriteLine("계획 ID: " + plan.PlanId);
        output.WriteLine("계획 크기: " + HumanText.Size(plan.Candidates.Sum(c => c.Bytes)) + " · 대상 " + plan.Candidates.Count + "개");
        if (!await ConfirmAsync(plan.PlanId, token)) return;
        var path = SafePath.ResolveInsideChecked(settings.Root, "console-retention-" + plan.PlanId + ".json");
        await SharedAsync(async _ => { await JsonFiles.WriteAsync(path, plan, false, token); return true; });
        // No shared lease survives into ApplyAsync; its existing offline locks and reference/hash checks remain authoritative.
        try { await RetentionMaintenance.ApplyAsync(settings, plan, true); }
        catch (IOException error)
        {
            output.WriteLine(error.Data["RetentionBeforeMutation"] is true
                ? "시작 전 잠금 대기: serve/watch 또는 다른 유지보수 작업을 정상 종료한 뒤 같은 계획으로 적용하세요. 이 시도는 삭제를 시작하지 않았습니다."
                : "I/O 오류: 일부 자료가 이미 처리됐을 수 있습니다. 계획과 journal을 보존하고 같은 계획으로 retention apply를 다시 실행해 이어서 처리하세요. 새 계획으로 대체하지 마세요.");
            output.WriteLine("UeDtLauncher.DistributionServer retention apply --config " + Quote(configPath) + " --plan " + Quote(path) + " --confirm");
            return;
        }
        output.WriteLine("정리 적용 완료. 선택한 계획의 자료만 처리했습니다.");
        try { File.Delete(path); output.WriteLine("이번 계획 파일 정리 완료."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { output.WriteLine("자료 정리는 완료됐지만 계획 파일이 남았습니다. 재적용하지 말고 계획 파일만 확인하세요: " + HumanText.Safe(path)); }
    }

    private static string Quote(string text) => "'" + text.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\\''") + "'";

    private async Task CredentialsAsync()
    {
        await SharedAsync(store =>
        {
            output.WriteLine("유형 | 관리 ID | 클라이언트 | 만료 | 폐기 | 만료 latch");
            foreach (var c in new DistributionTokens(store).List()) output.WriteLine($"토큰 | {HumanText.Safe(c.Id)} | {HumanText.Safe(c.Client)} | {HumanText.Safe(c.ExpiresAtUtc ?? "미지정")} | {c.Revoked} | {c.Expired}");
            foreach (var c in new DistributionDeviceKeys(store).List()) output.WriteLine($"PC 키 | {HumanText.Safe(c.KeyId)} | {HumanText.Safe(c.ClientId)} | {HumanText.Safe(c.ExpiresAtUtc ?? "미지정")} | {c.Revoked} | {c.Expired}");
            return Task.FromResult(true);
        });
    }
}
