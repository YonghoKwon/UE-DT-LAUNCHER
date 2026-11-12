using System.Net;

namespace UeDtLauncher;

public sealed class HttpStatusFailureException(string code, HttpStatusCode status, int? retryAfterSeconds = null)
    : HttpRequestException("Distribution request failed: " + code, null, status)
{
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public static class HttpFailures
{
    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
            int? seconds = wait is { } value ? (int)Math.Clamp(Math.Ceiling(value.TotalSeconds), 0, 86400) : null;
            throw new HttpStatusFailureException("rate-limited", response.StatusCode, seconds);
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized &&
            response.Headers.TryGetValues("X-UE-DT-Auth-Error", out var values) && values.Contains("stale-challenge"))
            throw new HttpStatusFailureException("stale-challenge", response.StatusCode);
        response.EnsureSuccessStatusCode();
    }

    public static int? RetryAfter(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var value = current switch
            {
                HttpStatusFailureException http => http.RetryAfterSeconds,
                AgentOperationException agent => agent.RetryAfterSeconds,
                _ => null
            };
            if (value is >= 0 and <= 86400) return value;
        }
        return null;
    }
}
