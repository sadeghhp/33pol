using Microsoft.AspNetCore.Http;

namespace Pol33.Api.Contracts;

public sealed class AdminModelTestResponse
{
    public bool Ok { get; set; }

    public string ModelId { get; set; } = string.Empty;

    public long LatencyMs { get; set; }

    public int? StatusCode { get; set; }

    public string? Detail { get; set; }

    public string? Content { get; set; }

    public int SuggestedStatusCode { get; set; } = StatusCodes.Status200OK;
}
