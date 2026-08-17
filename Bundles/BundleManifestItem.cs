namespace FikaHeadlessManager.Bundles;

// One entry of the server's /singleplayer/bundles response.
public sealed record BundleManifestItem
{
    public string FileName { get; set; } = string.Empty;

    public string ModPath { get; set; } = string.Empty;

    public uint Crc { get; set; }

    public long Size { get; set; }

    public long ModifiedUtcTicks { get; set; }

    public List<string> Dependencies { get; set; } = [];
}
