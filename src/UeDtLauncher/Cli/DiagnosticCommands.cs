using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private static async Task<int> RunDoctorAsync(string[] args)
    {
        var format = Get(args, "--format") ?? "json";
        if (format is not ("json" or "text")) throw new ArgumentException("doctor --format must be json or text.");
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var report = await LauncherDoctor.RunAsync(configPath, Has(args, "--online"));
        Console.WriteLine(format == "text" ? DoctorPresentation.FormatText(report) : DiagnosticRedactor.Redact(JsonSerializer.Serialize(report, JsonFiles.Options)));
        return report.Healthy ? 0 : 1;
    }

    private static async Task<int> RunDiagnosticsAsync(string[] args)
    {
        var action = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "export";
        if (!action.Equals("export", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("diagnostics supports only the export action.");
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var output = Get(args, "--output") ?? $"launcher-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        Console.WriteLine("Diagnostics written: " + await DiagnosticsExporter.ExportAsync(configPath, output));
        return 0;
    }

}
