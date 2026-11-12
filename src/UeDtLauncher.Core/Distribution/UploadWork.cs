namespace UeDtLauncher;

internal static class UploadWork
{
    internal static string CheckedPath(string root, string work)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullWork = Path.GetFullPath(work);
        if (string.Equals(fullRoot, fullWork, SafePath.FileSystemComparison)) throw new IOException("Upload cleanup cannot target its work root.");
        var checkedPath = SafePath.ResolveInsideChecked(fullRoot, Path.GetRelativePath(fullRoot, fullWork));
        var driveRoot = Path.GetPathRoot(fullRoot)!;
        SafePath.EnsureNoReparsePoints(driveRoot, checkedPath);
        return checkedPath;
    }

    internal static void DeleteOwned(string root, string work)
    {
        var path = CheckedPath(root, work);
        var expected = new HashSet<string>([Path.Combine(path, "payload.zip"), Path.Combine(path, "release.json")], SafePath.FileSystemComparer);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            if (!expected.Contains(entry) || (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Unexpected upload work content; preserve for inspection.");
        foreach (var file in expected) File.Delete(file);
        // Only the two owned files are removed. Never recursively delete newly added content or another work folder.
        Directory.Delete(path, recursive: false);
    }
}
