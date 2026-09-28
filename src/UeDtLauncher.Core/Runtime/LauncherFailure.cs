using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
namespace UeDtLauncher;

public sealed class AgentOperationException(string code,string correlationId,string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = code;
    public string CorrelationId { get; } = correlationId;
}
public sealed class AgentConnectionException(Exception inner) : IOException("Update service connection unavailable.",inner);
public static class LauncherFailure
{
    public static string Code(Exception error)
    {
        for(Exception? ex=error;ex is not null;ex=ex.InnerException)
        {
            if(ex is AgentOperationException agent) return agent.ErrorCode;
            if(ex is RuntimeBlockedException) return "runtime-blocked";
            if(ex is RollbackPreviewChangedException) return "backup-preview-changed";
            if(ex is AgentConnectionException) return "service-unavailable";
            if(ex is HttpRequestException http) return http.StatusCode switch
            { HttpStatusCode.Unauthorized=>"authentication-failed",HttpStatusCode.Forbidden=>"access-denied",_=>http.InnerException is AuthenticationException?"integrity-failed":"server-unavailable" };
            if(ex is AuthenticationException or CryptographicException or InvalidDataException)return "integrity-failed";
            if(ex is UnauthorizedAccessException)return "file-access-denied";
            if(ex is OperationCanceledException or TimeoutException)return "timeout";
            if(ex is IOException)return "storage-failed";
            if(ex is ArgumentException)return "configuration-invalid";
        }
        return "unknown";
    }
}
