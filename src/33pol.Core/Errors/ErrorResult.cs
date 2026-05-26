using System.Text.Json.Serialization;

namespace Pol33.Core.Errors;

public sealed class ErrorResult
{
    [JsonPropertyName("error")]
    public required ErrorBody Error { get; init; }

    public static ErrorResult FromCode(
        GatewayErrorCode code,
        string message,
        string type,
        string? param = null,
        IReadOnlyDictionary<string, object>? details = null) =>
        new()
        {
            Error = new ErrorBody
            {
                Message = message,
                Type = type,
                Code = code.ToCodeString(),
                Param = param,
                Details = details is null or { Count: 0 } ? null : new Dictionary<string, object>(details),
            },
        };
}

public sealed class ErrorBody
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("param")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Param { get; init; }

    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object>? Details { get; init; }
}
