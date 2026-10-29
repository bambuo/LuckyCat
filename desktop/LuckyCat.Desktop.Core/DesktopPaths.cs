namespace LuckyCat.Desktop.Core;

public static class DesktopPaths
{
    public const string AppFolderName = "LuckyCat";
    public const string DataDirEnvVar = "LUCKYCAT_DATA_DIR";

    public static string Resolve(string? overrideDir, string localAppDataRoot) =>
        string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(localAppDataRoot, AppFolderName)
            : Path.GetFullPath(overrideDir);

    public static string DatabasePath(string dataDir) => Path.Combine(dataDir, "luckycat.db");
    public static string LogDirectory(string dataDir) => Path.Combine(dataDir, "logs");
    public static string InstanceLockPath(string dataDir) => Path.Combine(dataDir, "instance.lock");
    public static string InstanceInfoPath(string dataDir) => Path.Combine(dataDir, "instance.json");
    public static string SettingsPath(string dataDir) => Path.Combine(dataDir, "desktop.json");
}
