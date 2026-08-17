namespace FikaHeadlessManager.Bundles;

public sealed class BundlePlan
{
    internal List<BundleManifestItem> Missing { get; } = [];

    public int Total { get; internal set; }

    public int Present
    {
        get { return Total - Missing.Count; }
    }

    public int MissingCount
    {
        get { return Missing.Count; }
    }

    public long MissingBytes
    {
        get { return Missing.Sum(bundle => bundle.Size); }
    }
}
