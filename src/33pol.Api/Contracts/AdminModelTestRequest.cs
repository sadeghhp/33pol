namespace Pol33.Api.Contracts;

public sealed class AdminModelTestRequest
{
    public string? Prompt { get; set; }

    public int? MaxTokens { get; set; }
}
