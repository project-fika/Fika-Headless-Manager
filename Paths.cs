namespace FikaHeadlessManager;

public static class Paths
{
    public static readonly string GameRoot = Environment.CurrentDirectory;

    private static readonly string RuntimeRoot =
        Directory.Exists(Path.Combine(GameRoot, "SPT_Runtime")) ? Path.Combine(GameRoot, "SPT_Runtime") : GameRoot;

    public static readonly string ClientExecutable = "EscapeFromTarkov.exe";
    public static readonly string HeadlessPlugin = Path.Combine("BepInEx", "plugins", "Fika", "Fika.Headless.dll");
    public static readonly string ClientLog = Path.Combine(GameRoot, "BepInEx", "LogOutput.log");
    public static readonly string Config = "HeadlessConfig.json";
    public static readonly string Patches = Path.Combine(RuntimeRoot, "SPT_Data", "Launcher", "Patches");
    public static readonly string BundleCache = Path.Combine(RuntimeRoot, "user", "cache", "bundles");
    public static readonly string BundleCacheManifest = Path.Combine(RuntimeRoot, "user", "cache", "bundleCache.json");
}
