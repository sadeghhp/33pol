namespace Pol33.Core.Errors;

public sealed record WrittenErrorResponse(
    int HttpStatusCode,
    ErrorResult Body,
    string Json);
