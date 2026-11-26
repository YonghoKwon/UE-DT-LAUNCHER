using System.Text.Json.Serialization;

namespace UeDtLauncher;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceEnrollmentBundle(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string ClientId,
    [property: JsonRequired] string KeyId,
    [property: JsonRequired] string PublicKeyPem,
    [property: JsonRequired] string HostName,
    [property: JsonRequired] string[] Ipv4Addresses,
    [property: JsonRequired] DateTimeOffset CreatedAtUtc);
