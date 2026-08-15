/* Ported from SPT's launcher (SPTarkov.Core.Patching)
 * License: NCSA Open Source License
 *
 * Copyright: SPT
 * AUTHORS:
 * waffle.lord
 * ArchangelWTF
 */

using Microsoft.Extensions.Logging;
using SharpHDiffPatch.Core;

namespace FikaHeadlessManager.Patching;

public class FilePatcher(ILogger<FilePatcher> logger)
{
    private const string BackupExtension = ".spt-bak";

    public PatchResult Run(string targetPath, string patchPath)
    {
        var patchFiles = new DirectoryInfo(patchPath).GetFiles("*.delta", SearchOption.AllDirectories);
        var patched = 0;

        foreach (var patchFile in patchFiles)
        {
            var relativePath = patchFile.FullName[patchPath.Length..].TrimStart('\\', '/');
            var targetFile = Path.Join(targetPath, relativePath.Replace(".delta", string.Empty));

            if (!Patch(targetFile, patchFile.FullName))
            {
                return new PatchResult(false, patched, patchFiles.Length);
            }

            patched++;
        }

        return new PatchResult(true, patched, patchFiles.Length);
    }

    // Deltas are built against the untouched files, so any previous patch has to be rolled back first.
    public void Restore(string targetPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        foreach (var backup in new DirectoryInfo(targetPath).EnumerateFiles($"*{BackupExtension}", options))
        {
            var targetFile = Path.ChangeExtension(backup.FullName, null);

            try
            {
                var current = new FileInfo(targetFile);
                if (current.Exists)
                {
                    current.IsReadOnly = false;
                    current.Delete();
                }

                File.Copy(backup.FullName, targetFile);
            }
            catch (Exception ex)
            {
                logger.LogError("Could not restore '{TargetFile}':\n{Message}", targetFile, ex.Message);
            }
        }
    }

    private bool Patch(string targetFile, string patchFile)
    {
        if (!File.Exists(targetFile))
        {
            logger.LogError("Could not patch '{TargetFile}', the file does not exist.", targetFile);
            return false;
        }

        var backupFile = $"{targetFile}{BackupExtension}";

        try
        {
            if (!File.Exists(backupFile))
            {
                File.Copy(targetFile, backupFile);
            }

            HDiffPatch.LogVerbosity = Verbosity.Quiet;

            var patcher = new HDiffPatch();
            patcher.Initialize(patchFile);
            patcher.Patch(backupFile, targetFile, false);
        }
        catch (Exception ex)
        {
            logger.LogError("Could not patch '{TargetFile}':\n{Message}", targetFile, ex.Message);
            return false;
        }

        return true;
    }
}
