using System.Text.Json;
using System.Text.Json.Serialization;
using Pol33.Core.Abstractions;

namespace Pol33.Core.Errors;

public sealed class OpenAiErrorResponseWriter : IErrorResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WrittenErrorResponse Write(
        GatewayErrorCode code,
        string? message = null,
        string? param = null,
        IReadOnlyDictionary<string, object>? details = null)
    {
        var definition = GatewayErrorCatalog.Get(code);
        var body = ErrorResult.FromCode(
            code,
            message ?? definition.DefaultMessage,
            definition.Type,
            param ?? definition.Param,
            details);

        return new WrittenErrorResponse(
            definition.HttpStatusCode,
            body,
            JsonSerializer.Serialize(body, SerializerOptions));
    }
}
