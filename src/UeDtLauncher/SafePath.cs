namespace UeDtLauncher;

public static class SafePath
{
    public static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Manifest file path is empty.");
        }

        var normalizedRelative = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelative))
        {
            throw new InvalidOperationException($"Manifest path must be relative: {relativePath}");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalizedRelative));
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) && !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Manifest path escapes install directory: {relativePath}");
        }

        return fullPath;
    }

    /// <summary>
    /// Resolves like <see cref="ResolveInside"/> and additionally rejects paths that pass through a
    /// symlink/reparse point below the root, so writes cannot be redirected outside the install tree.
    /// Use at apply/extract time; plain <see cref="ResolveInside"/> is enough for validation-only checks.
    /// </summary>
    public static string ResolveInsideChecked(string root, string relativePath)
    {
        var fullPath = ResolveInside(root, relativePath);
        EnsureNoReparsePoints(Path.GetFullPath(root), fullPath);
        return fullPath;
    }

    public static void EnsureNoReparsePoints(string fullRoot, string fullPath)
    {
        // The root itself may legitimately be a symlink (e.g. a relocated install dir);
        // only components strictly below the root are rejected.
        var current = fullPath;
        while (!string.IsNullOrEmpty(current)
               && !string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase)
               && current.Length > fullRoot.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException($"Refusing to write through a symlink inside the install directory: {current}");
                }
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
