using Pol33.Core.Errors;

namespace Pol33.Core.Abstractions;

public interface IErrorResponseWriter
{
    WrittenErrorResponse Write(
        GatewayErrorCode code,
        string? message = null,
        string? param = null,
        IReadOnlyDictionary<string, object>? details = null);
}
