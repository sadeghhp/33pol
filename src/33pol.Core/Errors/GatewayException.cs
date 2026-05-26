namespace Pol33.Core.Errors;

public sealed class GatewayException : Exception
{
    public GatewayException(GatewayErrorCode code, string message, string errorType, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        ErrorType = errorType;
    }

    public GatewayErrorCode Code { get; }

    public string ErrorType { get; }

    public ErrorResult ToErrorResult(string? param = null) =>
        ErrorResult.FromCode(Code, Message, ErrorType, param);
}
