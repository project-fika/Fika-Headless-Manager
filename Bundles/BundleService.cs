/* Ported from SPT's launcher (SPTarkov.Core.Helpers.BundleHelper)
 * License: NCSA Open Source License
 *
 * Copyright: SPT
 * AUTHORS:
 * ArchangelWTF
 */

using FikaHeadlessManager.Server;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;

namespace FikaHeadlessManager.Bundles;

public sealed class BundleService(ILogger<BundleService> logger, ServerClient server)
{
    private const string ManifestEndpoint = "singleplayer/bundles";
    private const string FileEndpoint = "files/bundle/";
    private const int MaxConcurrentDownloads = 8;
    private const int MaxAttemptsPerBundle = 3;
    private const int ReportIntervalMs = 100;

    public async Task<BundlePlan?> PlanAsync(CancellationToken token = default)
    {
        List<BundleManifestItem>? manifest;

        try
        {
            manifest = await server.GetJsonAsync<List<BundleManifestItem>>(ManifestEndpoint, token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not fetch the bundle manifest from the server.");
            return null;
        }

        var plan = new BundlePlan { Total = manifest?.Count ?? 0 };

        if (manifest != null)
        {
            plan.Missing.AddRange(manifest.Where(bundle => !IsAvailable(bundle)));
        }

        return plan;
    }

    public async Task<bool> DownloadAsync(BundlePlan plan, IProgress<BundleProgress>? progress, CancellationToken token = default)
    {
        if (plan.MissingCount == 0)
        {
            progress?.Report(new BundleProgress { Current = plan.Total, Total = plan.Total });
            return true;
        }

        var completed = plan.Present;
        var downloadedBytes = 0L;

        // Known up front from the manifest
        var totalBytes = plan.MissingBytes;

        var failures = new ConcurrentBag<string>();
        var started = Stopwatch.StartNew();

        var lastReportMs = 0L;
        var lastReportBytes = 0L;
        var speed = 0d;

        void Report(string bundleName)
        {
            var nowMs = (long)started.Elapsed.TotalMilliseconds;
            var previous = Interlocked.Read(ref lastReportMs);

            if (nowMs - previous < ReportIntervalMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref lastReportMs, nowMs, previous) != previous)
            {
                return;
            }

            var running = Interlocked.Read(ref downloadedBytes);
            var window = (nowMs - previous) / 1000d;

            if (window > 0)
            {
                speed = (running - Interlocked.Exchange(ref lastReportBytes, running)) / window;
            }

            progress?.Report(
                new BundleProgress
                {
                    Current = Volatile.Read(ref completed),
                    Total = plan.Total,
                    BundleName = bundleName,
                    DownloadedBytes = running,
                    TotalBytes = totalBytes,
                    BytesPerSecond = speed,
                    IsDownloading = true
                }
            );
        }

        await Parallel.ForEachAsync(
            plan.Missing,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDownloads, CancellationToken = token },
            async (bundle, ct) =>
            {
                var destination = CachePathFor(bundle);

                for (var attempt = 1; attempt <= MaxAttemptsPerBundle; attempt++)
                {
                    // Outside the try so a failed attempt can take its partial bytes back out of the
                    // running total before the retry starts counting from zero again
                    var lastReported = 0L;

                    try
                    {
                        await server.DownloadFileAsync(
                            FileEndpoint + bundle.FileName,
                            destination,
                            written =>
                            {
                                Interlocked.Add(ref downloadedBytes, written - lastReported);
                                lastReported = written;

                                Report(bundle.FileName);
                            },
                            ct
                        );

                        Interlocked.Increment(ref completed);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (attempt < MaxAttemptsPerBundle)
                    {
                        Interlocked.Add(ref downloadedBytes, -lastReported);
                        logger.LogWarning("Download of '{Bundle}' failed, attempt {Attempt}: {Message}", bundle.FileName, attempt, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Add(ref downloadedBytes, -lastReported);
                        logger.LogError("Giving up on '{Bundle}': {Message}", bundle.FileName, ex.Message);
                        failures.Add(bundle.FileName);
                        return;
                    }
                }
            }
        );

        if (!failures.IsEmpty)
        {
            logger.LogError("Failed to download {Count} bundle(s), e.g. '{Bundle}'.", failures.Count, failures.First());
            return false;
        }

        progress?.Report(
            new BundleProgress
            {
                Current = plan.Total,
                Total = plan.Total,
                DownloadedBytes = downloadedBytes,
                TotalBytes = totalBytes
            }
        );

        return true;
    }

    private static bool IsAvailable(BundleManifestItem bundle)
    {
        var cachePath = CachePathFor(bundle);

        if (File.Exists(cachePath) && new FileInfo(cachePath).Length == bundle.Size)
        {
            return true;
        }

        var modPath = ModPathFor(bundle);

        if (!File.Exists(modPath))
        {
            return false;
        }

        var info = new FileInfo(modPath);

        if (info.Length != bundle.Size)
        {
            return false;
        }

        // Same size and write time means this is the same file the server hashed
        if (info.LastWriteTimeUtc.Ticks == bundle.ModifiedUtcTicks)
        {
            return true;
        }

        return HashFile(modPath) == bundle.Crc;
    }

    private static string CachePathFor(BundleManifestItem bundle)
    {
        return Path.Join(Paths.BundleCache, bundle.Crc.ToString("X8"), bundle.FileName);
    }

    private static string ModPathFor(BundleManifestItem bundle)
    {
        return Path.Join(Paths.Runtime, bundle.ModPath, "bundles", bundle.FileName);
    }

    private static uint HashFile(string path)
    {
        var crc = new Crc32();
        using var stream = File.OpenRead(path);

        var buffer = new byte[81920];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            crc.Append(buffer.AsSpan(0, read));
        }

        return crc.GetCurrentHashAsUInt32();
    }
}
