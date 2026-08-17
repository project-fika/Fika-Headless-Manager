namespace FikaHeadlessManager;

public static class Paths
{
    public static readonly string GameRoot = Environment.CurrentDirectory;

    public static readonly string Runtime = Path.Combine(GameRoot, "SPT_Runtime");

    public static readonly string ClientExecutable = "EscapeFromTarkov.exe";
    public static readonly string HeadlessPlugin = Path.Combine("BepInEx", "plugins", "Fika", "Fika.Headless.dll");
    public static readonly string ClientLog = Path.Combine(GameRoot, "BepInEx", "LogOutput.log");
    public static readonly string Config = "HeadlessConfig.json";
    public static readonly string Patches = Path.Combine(Directory.Exists(Runtime) ? Runtime : GameRoot, "SPT_Data", "Launcher", "Patches");
    public static readonly string BundleCache = Path.Combine(Runtime, "user", "cache", "bundles");
}
