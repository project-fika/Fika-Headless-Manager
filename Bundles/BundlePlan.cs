using System.Collections.Concurrent;

namespace FikaHeadlessManager.Bundles;

public sealed class BundlePlan
{
    internal ConcurrentDictionary<string, BundleCacheEntry> Verified { get; } = [];

    internal List<BundleManifestItem> Stale { get; } = [];

    public int Total { get; internal set; }

    public int Cached
    {
        get { return Total - Stale.Count; }
    }

    public int Missing
    {
        get { return Stale.Count; }
    }

    public long MissingBytes
    {
        get { return Stale.Sum(bundle => bundle.Size); }
    }
}
