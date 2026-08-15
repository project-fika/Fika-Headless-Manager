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
using System.Text.Json;

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

        if (manifest == null)
        {
            return plan;
        }

        var cache = await LoadCacheAsync(token);

        foreach (var bundle in manifest)
        {
            if (TryValidate(Path.Join(Paths.BundleCache, bundle.FileName), bundle, cache, out var entry))
            {
                plan.Verified[bundle.FileName] = entry;
                continue;
            }

            plan.Stale.Add(bundle);
        }

        return plan;
    }

    public async Task<bool> AcquireAsync(BundlePlan plan, IProgress<BundleProgress>? progress, CancellationToken token = default)
    {
        var acquired = await DownloadAsync(plan, progress, token);

        await SaveCacheAsync(plan.Verified, token);

        return acquired;
    }

    private async Task<bool> DownloadAsync(BundlePlan plan, IProgress<BundleProgress>? progress, CancellationToken token)
    {
        if (plan.Stale.Count == 0)
        {
            progress?.Report(new BundleProgress { Current = plan.Total, Total = plan.Total });
            return true;
        }

        var completed = plan.Cached;
        var downloadedBytes = 0L;
        var totalBytes = plan.MissingBytes;

        var failures = new ConcurrentBag<string>();
        var started = Stopwatch.StartNew();

        var lastReportMs = 0L;
        var lastReportBytes = 0L;
        var speed = 0d;

        void Report(string bundleName, bool force)
        {
            var nowMs = (long)started.Elapsed.TotalMilliseconds;
            var previous = Interlocked.Read(ref lastReportMs);

            if (!force && nowMs - previous < ReportIntervalMs)
            {
                return;
            }

            if (!force && Interlocked.CompareExchange(ref lastReportMs, nowMs, previous) != previous)
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
            plan.Stale,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentDownloads, CancellationToken = token },
            async (bundle, ct) =>
            {
                var destination = Path.Join(Paths.BundleCache, bundle.FileName);

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

                                Report(bundle.FileName, false);
                            },
                            ct
                        );

                        var info = new FileInfo(destination);

                        plan.Verified[bundle.FileName] = new BundleCacheEntry
                        {
                            Size = info.Length,
                            ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                            Crc = bundle.Crc
                        };

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

    private bool TryValidate(string path, BundleManifestItem bundle, Dictionary<string, BundleCacheEntry> cache, out BundleCacheEntry entry)
    {
        entry = null!;

        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        var size = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;

        if (cache.TryGetValue(bundle.FileName, out var cached)
            && cached.Size == size
            && cached.ModifiedUtcTicks == modified
            && cached.Crc == bundle.Crc)
        {
            entry = cached;
            return true;
        }

        var crc = HashFile(path);

        if (crc != bundle.Crc)
        {
            return false;
        }

        entry = new BundleCacheEntry
        {
            Size = size,
            ModifiedUtcTicks = modified,
            Crc = crc
        };

        return true;
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

    private async Task<Dictionary<string, BundleCacheEntry>> LoadCacheAsync(CancellationToken token)
    {
        if (!File.Exists(Paths.BundleCacheManifest))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(Paths.BundleCacheManifest);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, BundleCacheEntry>>(stream, cancellationToken: token) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Bundle cache unreadable, rebuilding: {Message}", ex.Message);
            return [];
        }
    }

    /// <summary>Writes only the bundles seen this run, so entries for removed mods drop out.</summary>
    private async Task SaveCacheAsync(ConcurrentDictionary<string, BundleCacheEntry> entries, CancellationToken token)
    {
        try
        {
            var directory = Path.GetDirectoryName(Paths.BundleCacheManifest);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Create(Paths.BundleCacheManifest);
            await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not write the bundle cache: {Message}", ex.Message);
        }
    }
}
