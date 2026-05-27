namespace Pol33.Core.Abstractions;

/// <summary>
/// Scoped inference request metrics hook. Call <see cref="SetOutcome"/> before dispose when the result is known.
/// </summary>
public interface IInferenceRequestScope : IDisposable
{
    void SetOutcome(bool success, string? errorCode = null);
}
