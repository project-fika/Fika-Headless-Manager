using FikaHeadlessManager.Bundles;
using FikaHeadlessManager.Models;
using FikaHeadlessManager.Patching;
using FikaHeadlessManager.Server;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace FikaHeadlessManager;

public sealed class HeadlessRunner(
    ILogger<HeadlessRunner> logger,
    Settings settings,
    ServerClient server,
    PatchService patcher,
    BundleService bundles)
{
    private readonly PosixSignal[] _stopSignals = [PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM, PosixSignal.SIGHUP];
    private readonly List<PosixSignalRegistration> _signals = [];
    private readonly Lock _stopLock = new();

    private Process? _tarkovProcess;
    private bool _withGraphics;

    public async Task RunAsync(bool prepareOnly)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopGame();

        foreach (var signal in _stopSignals)
        {
            _signals.Add(PosixSignalRegistration.Create(signal, _ => StopGame()));
        }

        if (!patcher.PatchClient())
        {
            Exit();
        }

        while (true)
        {
            if (!await server.IsAccessibleAsync())
            {
                Exit();
            }

            if (!await AcquireBundlesAsync())
            {
                Exit();
            }

            if (prepareOnly)
            {
                logger.LogInformation("The client is patched and bundles are in place, exiting..");
                return;
            }

            _withGraphics = await WaitForGraphicsInput();

            if (!await StartGameAsync())
            {
                logger.LogError("Could not start the headless client!");
                Exit();
            }

            await _tarkovProcess!.WaitForExitAsync();
            _tarkovProcess = null;

            logger.LogInformation("Game exited, restarting...");
        }
    }

    private async Task<bool> AcquireBundlesAsync()
    {
        var plan = await AnsiConsole.Status().StartAsync("Verifying bundles...", _ => bundles.PlanAsync());

        if (plan == null)
        {
            return false;
        }

        if (plan.Total == 0)
        {
            logger.LogInformation("The server published no bundles.");
            return true;
        }

        if (plan.MissingCount == 0)
        {
            logger.LogInformation("All {Total} bundle(s) are already present.", plan.Total);
            return true;
        }

        logger.LogInformation("{Present} of {Total} bundle(s) present, {Missing} to download ({Size}).",
            plan.Present, plan.Total, plan.MissingCount, BundleProgress.FormatBytes(plan.MissingBytes));

        return await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new SpinnerColumn(),
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn()
            )
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Downloading bundles", maxValue: plan.MissingBytes);

                var progress = new LockedProgress<BundleProgress>(report =>
                {
                    task.Value = report.DownloadedBytes;
                    task.Description = report.BundleName.Length == 0
                        ? "Downloading bundles"
                        : $"{Markup.Escape(Path.GetFileName(report.BundleName))} [grey]({report.Current}/{report.Total}, {report.FileSizeInfo}, {report.DownloadSpeed})[/]";
                });

                return await bundles.DownloadAsync(plan, progress);
            });
    }

    private async Task<bool> StartGameAsync()
    {
        var logMessage = $"Starting headless client {(_withGraphics ? "with" : "without")} graphics and {(settings.ExtraLogging ? "extra logging" : "no extra logging")}.";

        if (!string.IsNullOrEmpty(settings.Title))
        {
            logMessage += $" Using custom title: '{settings.Title}'";
        }

        logger.LogInformation("{Message}", logMessage);

        if (File.Exists(Paths.ClientLog))
        {
            try
            {
                await Task.Run(() => File.Move(Paths.ClientLog, Paths.ClientLog.Replace(".log", "_prev.log"), true));
            }
            catch (Exception ex)
            {
                logger.LogError("Could not archive the previous log file:\n{Message}", ex.Message);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            Arguments = BuildStartArguments(),
            UseShellExecute = true,
            FileName = Paths.ClientExecutable,
            WindowStyle = (!_withGraphics && settings.StartMinimized) ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
        };

        _tarkovProcess = Process.Start(startInfo);
        return _tarkovProcess != null;
    }

    private string BuildStartArguments()
    {
        var graphicsArgs = _withGraphics ? string.Empty : " -nographics -batchmode";
        var logArg = !settings.ExtraLogging ? string.Empty : " -logfile Headless.log";
        var titleArg = !string.IsNullOrEmpty(settings.Title) ? $" -title=\"{settings.Title}\"" : string.Empty;

        return $"-token={settings.ProfileId} " +
               $"-config={{'BackendUrl':'{settings.BackendUrl!.OriginalString}','Version':'live'}}" +
               graphicsArgs +
               logArg +
               titleArg +
               " --enable-console true";
    }

    private async Task<bool> WaitForGraphicsInput()
    {
        logger.LogInformation("Press 'g' to start with graphics or wait 3 seconds...");

        var delayTask = Task.Delay(3000);

        while (!delayTask.IsCompleted)
        {
            if (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                return key.Key == ConsoleKey.G;
            }

            await Task.Delay(50); // small delay to avoid busy looping
        }

        return false;
    }

    private void StopGame()
    {
        lock (_stopLock)
        {
            try
            {
                if (_tarkovProcess is { HasExited: false })
                {
                    _tarkovProcess.Kill(true);
                }
            }
            catch (Exception) {}
        }
    }

    [DoesNotReturn]
    private static void Exit()
    {
        if (!Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("Press any key to exit...");
            Console.ReadKey(true);
        }

        Environment.Exit(1);
    }

    private sealed class LockedProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Lock _lock = new();

        public void Report(T value)
        {
            lock (_lock)
            {
                report(value);
            }
        }
    }
}
