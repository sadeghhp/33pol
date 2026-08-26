using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Turns a request's identity and model into the set of rules that governs it, resolving every
/// scope's tier and applying any adaptive reduction.
/// </summary>
public interface IRateLimitPlanResolver
{
    /// <summary>
    /// Whether rate limiting is enforced at all. Read per request from the live config snapshot, so
    /// toggling it in the admin UI takes effect without a restart. Callers must check this before
    /// resolving a plan.
    /// </summary>
    bool IsEnabled();

    /// <summary>
    /// The rule set for this subject and model.
    /// </summary>
    /// <param name="modelId">
    /// Null before the request body has been parsed. The model-independent rules are identical
    /// either way, so a caller may resolve once without a model to gate the parse and again with one
    /// — the second call reuses the same cached rules for the first stage.
    /// </param>
    RateLimitPlan Resolve(in RateLimitSubject subject, string? modelId);
}
