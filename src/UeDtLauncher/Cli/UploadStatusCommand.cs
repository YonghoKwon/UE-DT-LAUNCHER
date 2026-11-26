using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private sealed record UploadStatusRequest(string UploadId, string Format, bool Wait, TimeSpan Timeout,
        TimeSpan Interval, UploadProfile Profile, UploadStep Step);

    private sealed record UploadStatusObservation(UploadStatus Status, JsonElement Document, DateTimeOffset QueriedAtUtc)
    {
        public void Write(TextWriter output, string format)
        {
            if (format == "json")
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer, new() { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var field in Document.EnumerateObject())
                    {
                        writer.WritePropertyName(field.Name);
                        writer.WriteRawValue(field.Value.GetRawText());
                    }
                    writer.WriteString("queriedAtUtc", QueriedAtUtc);
                    writer.WriteEndObject();
                }
                output.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
                return;
            }
            var detail = Status.Reason ?? Status.State switch
            {
                "transferring" => "파일을 전송하는 중입니다.",
                "waiting" => "서버 접수를 기다리는 중입니다.",
                "processing" => "서버가 패키지를 검사하는 중입니다.",
                "pending" => "관리자 승인을 기다리는 중입니다.",
                "publishing" => "서버가 릴리스를 게시하는 중입니다.",
                "published" => "릴리스가 게시되었습니다.",
                "recommended" => "이 릴리스가 트랙의 현재 추천판입니다.",
                _ => throw new InvalidOperationException("Invalid upload status.")
            };
            output.WriteLine($"{Status.Label} — {detail}");
        }
    }

    internal static async Task<int> UploadStatusAsync(string[] args, TextWriter output, TextWriter errors,
        IUploadProcessRunner runner)
    {
        UploadStatusRequest request;
        try { request = await ReadUploadStatusRequestAsync(args); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or
            JsonException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            errors.WriteLine("입력 오류 — 업로드 ID, SSH 프로필, 상태 경로와 조회 옵션을 확인하세요. upload status 명령은 --help에서 확인할 수 있습니다.");
            return 2;
        }

        using var deadline = new CancellationTokenSource(request.Timeout);
        UploadStatusObservation? last = null;
        var consecutiveFailures = 0;
        try
        {
            while (true)
            {
                var response = await runner.CaptureAsync(request.Step.Executable, request.Step.Arguments,
                    UploadStatus.MaximumBytes, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (response.ExitCode != 0)
                {
                    if (!request.Wait || ++consecutiveFailures == 3)
                    {
                        errors.WriteLine(response.ExitCode == 255
                            ? UploadFailureGuidance.For("status", 255, request.Profile)
                            : "상태 파일을 읽을 수 없습니다. 업로드 ID와 서버의 상태 폴더 권한을 확인하세요.");
                        return 1;
                    }
                }
                else
                {
                    var status = UploadStatus.Parse(response.StandardOutput, request.UploadId, out var document);
                    last = new(status, document, DateTimeOffset.UtcNow);
                    consecutiveFailures = 0;
                    var result = request.Wait ? status.State switch
                    {
                        "transferring" or "waiting" or "processing" => (int?)null,
                        "failed" or "rejected" => 3,
                        _ => 0
                    } : 0;
                    if (result is { } exit)
                    {
                        last.Write(output, request.Format);
                        return exit;
                    }
                }
                await Task.Delay(request.Interval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            last?.Write(output, request.Format);
            errors.WriteLine("업로드 상태 조회 제한시간이 지났습니다.");
            return request.Wait ? 4 : 1;
        }
        catch (UploadExecutableMissingException)
        {
            errors.WriteLine(UploadFailureGuidance.MissingExecutable);
            return 1;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            errors.WriteLine("서버의 업로드 상태 응답 형식 또는 크기를 확인하세요.");
            return 1;
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            errors.WriteLine("OpenSSH를 실행하거나 상태 파일을 읽을 수 없습니다.");
            return 1;
        }
    }

    private static async Task<UploadStatusRequest> ReadUploadStatusRequestAsync(string[] args)
    {
        var uploadId = Required(args, "--upload-id");
        ReleaseSidecar.Segment(uploadId);
        var format = Get(args, "--format") ?? "text";
        if (format is not ("text" or "json")) throw new CliArgumentException("--format requires text or json.");
        var wait = Has(args, "--wait");
        var timeout = UploadStatusSeconds(args, "--timeout", 1800);
        var interval = UploadStatusSeconds(args, "--interval", 5);
        var port = Get(args, "--port") is null ? (int?)null : UploadStatusNumber(args, "--port", 22, 65535);
        var profile = await ReadUploadProfileAsync(args, port, Get(args, "--remote-status"));
        var directory = profile.ResolveStatusDirectory();
        var step = UploadTransport.Ssh(profile, "status", $"cat -- {directory}/{uploadId}.json", closeInput: true);
        return new(uploadId, format, wait, timeout, interval, profile, step);
    }

    private static TimeSpan UploadStatusSeconds(string[] args, string option, int fallback) =>
        TimeSpan.FromSeconds(UploadStatusNumber(args, option, fallback, 4_294_967));

    private static int UploadStatusNumber(string[] args, string option, int fallback, int maximum)
    {
        if (Get(args, option) is not { } value) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > maximum)
            throw new CliArgumentException(option + " requires a positive integer in range.");
        return number;
    }
}
