using FikaHeadlessManager.Models;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FikaHeadlessManager;

public static class Program
{
    private static Settings? Settings { get; set; }
    private static string? StartArguments
    {
        get
        {
            if (Settings == null)
            {
                Log("Settings were null when trying to generate StartArguments?", ConsoleColor.Red);
                return string.Empty;
            }

            if (string.IsNullOrEmpty(Settings.ProfileId))
            {
                Log("ProfileId was null!", ConsoleColor.Red);
                return string.Empty;
            }

            if (Settings.BackendUrl == null)
            {
                Log("BackendUrl was null!", ConsoleColor.Red);
                return string.Empty;
            }

            var graphicsArgs = WithGraphics ? string.Empty : " -nographics -batchmode";

            return $"-token={Settings.ProfileId} " +
                   $"-config={{'BackendUrl':'{Settings.BackendUrl.OriginalString}','Version':'live'}}" +
                   $"{graphicsArgs} --enable-console true";
        }
    }
    private static bool WithGraphics { get; set; }
    private static Process? TarkovProcess { get; set; }

    private static async Task Main()
    {
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

        if (!File.Exists("EscapeFromTarkov.exe"))
        {
            Log("Unable to find 'EscapeFromTarkov.exe'.\n" +
                "Make sure you are running Fika Headless Manager from a valid SPT install folder!", ConsoleColor.Red);
            Console.ReadKey(true);
            Environment.Exit(1);
        }

        if (!File.Exists(@"BepInEx\plugins\Fika\Fika.Headless.dll"))
        {
            Log("Unable to find 'Fika.Headless.dll'.\n" +
                "Please revisit the documentation and install Fika Headless using Fika-Installer!", ConsoleColor.Red);
            Console.ReadKey(true);
            Environment.Exit(1);
        }

        const string configPath = "HeadlessConfig.json";
        if (!File.Exists(configPath))
        {
            Log("Unable to find the configuration file 'HeadlessConfig.json'.\nMake sure that you have configured the headless correctly!", ConsoleColor.Red);
            Console.ReadKey(true);
            Environment.Exit(1);
        }

        try
        {
            await using var fileStream = File.OpenRead(configPath);
            Settings = await JsonSerializer.DeserializeAsync<Settings>(fileStream)
                       ?? throw new InvalidOperationException("Failed to deserialize configuration.");
        }
        catch (Exception ex)
        {
            Log($"Error loading configuration: {ex.Message}", ConsoleColor.Red);
            Console.ReadKey(true);
            Environment.Exit(1);
        }

        _ = Task.Run(GameLoop);
        await Task.Delay(-1); // keep process alive
    }

    private static void CurrentDomain_ProcessExit(object? sender, EventArgs e)
    {
        if (TarkovProcess == null)
        {
            return;
        }

        if (!TarkovProcess.HasExited)
        {
            TarkovProcess.Kill(true);
        }
    }

    private static async Task<bool> StartGame()
    {
        Log($"Starting headless client {(WithGraphics ? "with" : "without")} graphics.");

        var logFile = Path.Combine(Environment.CurrentDirectory, @"BepInEx\LogOutput.log");

        if (File.Exists(logFile))
        {
            try
            {
                await Task.Run(() => File.Move(logFile, logFile.Replace(".log", "_prev.log"), true));
            }
            catch (Exception ex)
            {
                Log($"Could not archive the previous log file:\n{ex.Message}", ConsoleColor.Red);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            Arguments = StartArguments,
            UseShellExecute = true,
            FileName = "EscapeFromTarkov.exe",
            WindowStyle = (!WithGraphics && Settings!.StartMinimized) ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
        };

        TarkovProcess = Process.Start(startInfo);
        return TarkovProcess != null;
    }

    private static async Task GameLoop()
    {
        while (true)
        {
            var success = await IsServerAccessible(Settings!.BackendUrl);
            if (!success)
            {
                Log("Press any key to exit...");
                Console.ReadKey(true);
                Environment.Exit(1);
            }

            WithGraphics = await WaitForGraphicsInput();

            var started = await StartGame();
            if (!started)
            {
                Log("Could not start the headless client!", ConsoleColor.Red);
                Console.ReadKey();
                Environment.Exit(1);
            }

            await TarkovProcess!.WaitForExitAsync();
            TarkovProcess = null;

            Log("Game exited, restarting...");
        }
    }

    private static async Task<bool> WaitForGraphicsInput()
    {
        Log("Press 'g' to start with graphics or wait 3 seconds...");

        var delayTask = Task.Delay(3000);

        while (!delayTask.IsCompleted)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                return key.Key == ConsoleKey.G;
            }
            await Task.Delay(50); // small delay to avoid busy looping
        }

        return false;
    }

    private static void Log(string message, ConsoleColor color = ConsoleColor.White)
    {
        if (color is not ConsoleColor.White)
        {
            Console.ForegroundColor = color;
        }

        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static async Task<bool> IsServerAccessible(Uri? BackendUrl, string ApiEndpoint = "fika/presence/get")
    {
        HttpClientHandler InsecureHandler = new()
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        HttpClient client = new(InsecureHandler);

        try
        {
            client.DefaultRequestHeaders.Add("responsecompressed", "0");

            var response = await client.GetAsync($"{BackendUrl}{ApiEndpoint}");

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                Log($"Could not access {BackendUrl}{ApiEndpoint}\nEnsure Fika Server mod is installed. Please review the installation process in the documentation.", ConsoleColor.Red);
                return false;
            }
        }
        catch
        {
            Log($"Could not reach SPT.Server at {BackendUrl}\nPlease ensure SPT.Server is running and accessible.", ConsoleColor.Red);
            return false;
        }
        finally
        {
            client.Dispose();
            InsecureHandler.Dispose();
        }
    }
}

internal static class StartupNative
{
    const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [ModuleInitializer]
    internal static void Init()
    {
        IntPtr h = LoadLibraryEx("winhttp.dll", IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);

        if (h == IntPtr.Zero)
        {
            // Considering this is a non-fatal error and this patch fix is only for rare cases we continue without throwing
            //throw new Win32Exception(Marshal.GetLastWin32Error());

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to load winhttp.dll from system folder.");
            Console.ResetColor();
            Console.WriteLine($"If no other issues are experienced, ignore this error.");
        }
    }
}