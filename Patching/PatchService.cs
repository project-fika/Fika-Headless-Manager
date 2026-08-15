using Microsoft.Extensions.Logging;

namespace FikaHeadlessManager.Patching;

public sealed class PatchService(ILogger<PatchService> logger, FilePatcher patcher)
{
    public bool PatchClient()
    {
        if (!Directory.Exists(Paths.Patches))
        {
            logger.LogWarning("No client patches were found, continuing with the client as-is.");
            return true;
        }

        logger.LogInformation("Patching the client...");
        patcher.Restore(Paths.GameRoot);

        foreach (var patch in Directory.GetDirectories(Paths.Patches))
        {
            var name = Path.GetFileName(patch);
            var result = patcher.Run(Paths.GameRoot, patch);

            if (!result.Ok)
            {
                logger.LogError("Failed to patch the client with '{Patch}', patched {Patched} of {Total} file(s).", name, result.Patched, result.Total);
                return false;
            }

            logger.LogInformation("Patched {Patched} file(s) with '{Patch}'.", result.Patched, name);
        }

        return true;
    }
}
