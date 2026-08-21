using FikaHeadlessManager.Bundles;
using FikaHeadlessManager.Logging;
using FikaHeadlessManager.Models;
using FikaHeadlessManager.Patching;
using FikaHeadlessManager.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace FikaHeadlessManager;

public static class Program
{
    private static async Task Main(string[] args)
    {
        var prepareOnly = ParseArguments(args);

        var settings = await LoadSettings();

        if (!string.IsNullOrEmpty(settings.Title))
        {
            Console.Title = $"Headless Manager - {settings.Title}";
        }

        await using var services = new ServiceCollection()
            .AddLogging(builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddProvider(new SpectreLoggerProvider()))
            .AddSingleton(settings)
            .AddSingleton<ServerClient>()
            .AddSingleton<FilePatcher>()
            .AddSingleton<PatchService>()
            .AddSingleton<BundleService>()
            .AddSingleton<HeadlessRunner>()
            .BuildServiceProvider();

        await services.GetRequiredService<HeadlessRunner>().RunAsync(prepareOnly);
    }

    private static bool ParseArguments(string[] args)
    {
        var prepareOnly = false;

        foreach (var arg in args)
        {
            if (arg.Equals("--prepare", StringComparison.OrdinalIgnoreCase))
            {
                prepareOnly = true;
                continue;
            }

            Fatal($"Unknown argument '{arg}'." + Environment.NewLine + "Usage: FikaHeadlessManager [--prepare]");
        }

        return prepareOnly;
    }

    private static async Task<Settings> LoadSettings()
    {
        if (!File.Exists(Paths.ClientExecutable))
        {
            Fatal("Unable to find 'EscapeFromTarkov.exe'.\n" +
                  "Make sure you are running Fika Headless Manager from a valid SPT install folder!");
        }

        if (!File.Exists(Paths.HeadlessPlugin))
        {
            Fatal("Unable to find 'Fika.Headless.dll'.\n" +
                  "Please revisit the documentation and install Fika Headless using Fika-Installer!");
        }

        if (!File.Exists(Paths.Config))
        {
            Fatal("Unable to find the configuration file 'HeadlessConfig.json'.\n" +
                  "Make sure that you have configured the headless correctly!");
        }

        try
        {
            await using var fileStream = File.OpenRead(Paths.Config);
            var settings = await JsonSerializer.DeserializeAsync<Settings>(fileStream)
                           ?? throw new InvalidOperationException("Failed to deserialize configuration.");

            if (string.IsNullOrEmpty(settings.ProfileId))
            {
                Fatal("ProfileId was null!");
            }

            if (settings.BackendUrl == null)
            {
                Fatal("BackendUrl was null!");
            }

            return settings;
        }
        catch (Exception ex)
        {
            Fatal($"Error loading configuration: {ex.Message}");
            throw; // Fatal exits the process, this only satisfies the compiler
        }
    }

    [DoesNotReturn]
    private static void Fatal(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");

        if (!Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("Press any key to exit...");
            Console.ReadKey(true);
        }

        Environment.Exit(1);
    }
}
