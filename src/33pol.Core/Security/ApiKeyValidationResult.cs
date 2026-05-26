namespace Pol33.Core.Security;

public enum ApiKeyValidationStatus
{
    Success,
    Missing,
    Invalid,
}

public sealed class ApiKeyValidationResult
{
    public static ApiKeyValidationResult Success { get; } = new(ApiKeyValidationStatus.Success);

    public ApiKeyValidationResult(ApiKeyValidationStatus status)
    {
        Status = status;
    }

    public ApiKeyValidationStatus Status { get; }

    public bool IsSuccess => Status == ApiKeyValidationStatus.Success;
}
