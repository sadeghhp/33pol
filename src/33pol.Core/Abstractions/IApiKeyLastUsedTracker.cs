namespace Pol33.Core.Abstractions;

public interface IApiKeyLastUsedTracker
{
    ValueTask TouchAsync(Guid apiKeyId, DateTimeOffset atUtc, CancellationToken cancellationToken = default);
}
