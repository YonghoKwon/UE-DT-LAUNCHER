using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private static readonly string[] ValueOptions =
    [
        "--server", "--remote-incoming", "--remote-status", "--port", "--identity-file", "--upload-id", "--timeout",
        "--allowed-profiles",
        "--allowlist",
        "--app-id",
        "--auth",
        "--backup",
        "--base-url",
        "--base-url-root",
        "--cache",
        "--catalog",
        "--catalog-profile",
        "--channel",
        "--client",
        "--config",
        "--credential-name",
        "--deployment-mode",
        "--destination-root",
        "--display-name",
        "--endpoint",
        "--entry-point",
        "--environment",
        "--executable-paths",
        "--format",
        "--hero-path",
        "--id",
        "--interval",
        "--key-id",
        "--kind",
        "--manifest",
        "--manifest-signature-url",
        "--manifest-url",
        "--mode",
        "--name",
        "--notes",
        "--notes-file",
        "--operation-id",
        "--output",
        "--package-dir",
        "--payload-root",
        "--platform",
        "--private-key",
        "--profile",
        "--project",
        "--project-id",
        "--project-profile",
        "--public-key",
        "--public-out",
        "--resume-id",
        "--server-root",
        "--server-url",
        "--signing-key-id",
        "--storage",
        "--thumbnail-path",
        "--version",
        "--version-policy",
        "--zip",
    ];

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine("Unknown command. Use --help to list supported commands.");
        PrintHelp();
        return 1;
    }

    private static string Required(string[] args, string name)
    {
        var value = Get(args, name);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Missing required argument: {name}");
        return value;
    }

    private static string? Get(string[] args, string name)
        => CliArguments.Get(args, name);

    private static bool Has(string[] args, string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    internal static void PrintHelp(TextWriter? output = null)
    {
        output ??= Console.Out;
        output.WriteLine("--error-details: show sanitized exception type chain without messages or stacks.");
        output.WriteLine("UE-DT-LAUNCHER");
        output.WriteLine();
        output.WriteLine("Current commands (distribution server mode):");
        output.WriteLine("  upload init --output upload.profile.json [--force]");
        output.WriteLine("  upload project-init --output ma0t10.project.json [--force]");
        output.WriteLine("  upload check --profile upload.profile.json (또는 --server user@host --remote-incoming /dt/store/incoming)");
        output.WriteLine("    키 인증 전용(BatchMode): 비밀번호를 묻지 않습니다. 최초 호스트 키 등록은 터미널 ssh로 확인하세요.");
        output.WriteLine("  upload status --upload-id <ID> [--profile upload.profile.json] [--remote-status /dt/store/upload-status] [--wait] [--timeout <초>] [--interval <초>] [--format text|json]");
        output.WriteLine("    읽기 전용 SSH 조회. --wait 기본 제한 1800초, 간격 5초. 종료 0 성공, 1 조회 실패, 2 입력 오류, 3 검사 실패·거절, 4 제한시간 초과.");
        output.WriteLine("  upload --zip package.zip --project-id id --version version --platform windows-x64|linux-x64 --entry-point app [--profile upload.profile.json] [--server user@host --remote-incoming /dt/store/incoming] [--port 22] [--identity-file path] [--upload-id id] [--dry-run]");
        output.WriteLine("  upload --profile upload.profile.json --project-profile ma0t10.project.json --zip Windows.zip --version 1.4.0 --notes-file notes.md --dry-run");
        output.WriteLine("    실제 접속 정보를 준비한 뒤 --dry-run을 빼면 전송합니다.");
        output.WriteLine("    명령줄 > 프로젝트 프로필 > 기본값. --project-profile만 주는 dry-run은 예시 접속 정보를 사용합니다.");
        output.WriteLine("    공통 메타데이터: --payload-root --display-name --environment --channel --executable-paths --hero-path --thumbnail-path");
        output.WriteLine("    --notes 또는 --notes-file <UTF-8 파일, 최대 64 KiB> 중 하나를 사용합니다.");
        output.WriteLine("    기본 출력: 한국어 요약. --format json으로 기존 JSON 출력. 진행 안내는 stderr에 표시합니다.");
        output.WriteLine("    성공 시 이번 실행의 작업 사본을 정리합니다. --keep-work로 보존할 수 있으며 실패 시에는 항상 보존합니다.");
        output.WriteLine("  runtime inspect --config <file> [--version <exact version>]");
        output.WriteLine("  runtime recover --config <file> --dry-run|--confirm-stopped [--version <exact version>] [--service-selection]");
        output.WriteLine("  release-metadata --zip <zip> --project-id <id> --version <version> --platform <platform> --entry-point <path> [--payload-root <path>] [--output <release.json>]");
        output.WriteLine("  release-metadata --project-profile ma0t10.project.json --zip Windows.zip --version 1.4.0 [--notes-file notes.md] [--output release.json]");
        output.WriteLine("  gui                 (also: --gui forces GUI; --cli forces CLI -> defaults to 'run')");
        output.WriteLine("  sample-config --output launcher.config.json [--mode distribution|managed-client|legacy-catalog] [--force]");
        output.WriteLine("    managed-client: --project-id id [--platform windows-x64|linux-x64] [--environment prod|dev] [--channel stable|beta|dev] [--version-policy latest|exact] [--version exact-version]");
        output.WriteLine("    [--server-url http://10.20.30.40] [--project-id id] [--platform windows-x64|linux-x64] [--profile general|developer]");
        output.WriteLine("    [--deployment-mode managed-agent|portable] [--auth request-signature-v1|bearer] [--credential-name name] [--signing-key-id id] [--public-key path]");
        output.WriteLine("  credential keygen --name name --key-id pc-key --public-out device-public.json [--storage managed|portable]");
        output.WriteLine("  enroll prepare --name name --key-id id --client pc-id --output folder [--storage managed|portable] [--force]");
        output.WriteLine("  enroll finish --server-url origin --project-id id --platform windows-x64|linux-x64 --credential-name name --signing-key-id id --public-key pem --output config.json [--storage managed|portable] [--force]");
        output.WriteLine("    Managed: protected release-public.pem in Agent config. Finish saves <output>.doctor.json.");
        output.WriteLine("  operation status|cancel|resume|discard --id <operation-id> [--config portable.json] [--endpoint pipe-or-socket]");
        output.WriteLine("  scheduled-check --config <config.json> (one query/status cycle; default disabled, explicit interval required; never installs/starts apps)");
        output.WriteLine("  run --config config.json [--operation-id <32-hex-id>] [--no-launch]; persistent resume requires performance.resumeCacheBytes");
        output.WriteLine("  credential repair-permissions --name name --dry-run|--apply [--storage managed|portable]");
        output.WriteLine("  run --config launcher.config.json [--repair] [--no-launch]");
        output.WriteLine("  rollback --config launcher.config.json [--list] [--backup <timestamp>]");
        output.WriteLine("  agent [status] [--endpoint <pipe-or-socket>] [--project <id>]");
        output.WriteLine("  credential <set|status|delete> --name <credential-name>");
        output.WriteLine("  generate-signing-key --private-key <private.pem> --public-key <public.pem>");
        output.WriteLine("  doctor --config launcher.config.json [--online] [--format json|text]");
        output.WriteLine("  diagnostics export --config launcher.config.json [--output <diagnostics.zip>]");
        output.WriteLine("Compatibility commands (static Manifest/Catalog mode):");
        output.WriteLine("  generate-manifest --package-dir <dir> --base-url <url> --entry-point <relative path> --version <version> [--app-id <id>] --output <manifest.json>");
        output.WriteLine("  update-catalog --catalog <catalog.json> --project-id <id> --version <version> --environment <prod|dev> --channel <stable|beta|dev> --platform <windows-x64|linux-x64> --manifest-url <url> [--display-name <name>] [--allowed-profiles general,developer] [--notes <text>] [--set-latest] [--remove] [--remove-project-if-empty]");
        output.WriteLine("  list-releases --catalog <catalog.json> [--project <id>]");
        output.WriteLine("  generate-nginx-acl --allowlist <project-ip-allowlist.json> [--output <acl.conf>]");
        output.WriteLine("  sign-manifest --manifest <manifest.json> --private-key <private.pem> [--key-id <id>] --output <manifest.json.sig>");
        output.WriteLine("  service --config launcher.config.json [--interval <seconds>] [--once]");
        output.WriteLine("  publish-release --package-dir <dir> --server-root <dir> --base-url-root <url> --project-id <id> --version <version> --platform <platform> --entry-point <path> --private-key <pem> --key-id <id> [--dry-run] [--replace] [--set-latest]");
    }
}
