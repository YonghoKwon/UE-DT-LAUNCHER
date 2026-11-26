namespace UeDtLauncher.Distribution;

public sealed record CredentialMetadata(string Id, string Client, bool Revoked, string? ExpiresAtUtc, bool Expired);

internal static class CredentialLifecycle
{
    internal static string? Expiry(DateTimeOffset? expiry) => expiry?.ToUniversalTime().ToString("O");
    internal static bool Due(string? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is null) return false;
        if (!DateTimeOffset.TryParseExact(expiresAt, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var expiry)) throw new InvalidDataException("Invalid credential expiry.");
        return now >= expiry;
    }
    internal static void LatchExpiry(IntakeStore store, bool deviceKey, string id) =>
        store.Credentials.LatchExpiry(deviceKey, id);
}
