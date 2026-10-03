using System.Reflection;

namespace UeDtLauncher;

public enum LauncherEdition { General, Developer }

public static class LauncherBuildInfo
{
#if LAUNCHER_DEVELOPER
    public const LauncherEdition Edition = LauncherEdition.Developer;
#else
    public const LauncherEdition Edition = LauncherEdition.General;
#endif
    public static string Profile => Edition == LauncherEdition.Developer ? "developer" : "general";
    public static string Version => typeof(LauncherBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static string Platform => OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    public const string UpdateNotice = "런처 업데이트는 새 설치본 또는 ZIP으로 진행해 주세요. 기존 자동 교체 자료는 보존됩니다.";
}
