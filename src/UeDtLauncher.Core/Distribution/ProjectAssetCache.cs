namespace UeDtLauncher;

public static class ProjectAssetCache
{
    public static async Task<string?> GetAsync(RemoteProjectAsset? asset, LauncherConfig config, string cacheRoot, CancellationToken token)
    {
        if (asset is null) return null;
        try
        {
            if (asset.Size <= 0 || asset.Size > 20 * 1024 * 1024 ||
                !System.Text.RegularExpressions.Regex.IsMatch(asset.Sha256, "^[0-9a-fA-F]{64}$")) return null;
            var uri = new Uri(asset.Url); LauncherConfigValidator.ValidateUrl(config, uri, "project image");
            var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp")) return null;
            Directory.CreateDirectory(cacheRoot);
            var target = Path.Combine(cacheRoot, asset.Sha256.ToLowerInvariant() + extension);
            if (await Hashing.Sha256MatchesAsync(target, asset.Sha256, token)) return target;
            var scratch = target + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using var client = SecureHttpClientFactory.Create(config);
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using (var output = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[81920]; long total = 0; int read;
                    while ((read = await input.ReadAsync(buffer, token)) != 0)
                    {
                        total += read; if (total > asset.Size) throw new InvalidDataException("Image exceeds declared size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (total != asset.Size) throw new InvalidDataException("Image size mismatch.");
                }
                if (!await Hashing.Sha256MatchesAsync(scratch, asset.Sha256, token)) return null;
                File.Move(scratch, target, true); return target;
            }
            finally { if (File.Exists(scratch)) File.Delete(scratch); }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or UriFormatException) { return null; }
    }
}
