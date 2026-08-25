using Pol33.Core.Models.Overview;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Policy;

/// <summary>
/// Who is being refused, by which control, and for what — the dimensions behind the Overview's
/// policy card. Keeps bounded per-key minute rings so "top tenants in the last hour" is an O(keys)
/// read, and never grows past <see cref="MaxKeysPerDimension"/> distinct keys per dimension.
/// </summary>
/// <remarks>
/// Tenant keys are tenant ids (or the anonymous partition) — never names — so nothing here is
/// sensitive beyond what the summary already carries. In-memory only; resets with the process.
/// </remarks>
public sealed class PolicyPressureTracker(TimeProvider? timeProvider = null)
{
    public const int MaxKeysPerDimension = CountDimension.DefaultMaxKeys;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly CountDimension _byReason = new();
    private readonly CountDimension _byTenant = new();
    private readonly CountDimension _byModel = new();
    private readonly CountDimension _unknownModels = new();
    private readonly CountDimension _grantDenials = new();
    private readonly CountDimension _budgets = new();

    public void RecordRejection(RejectionReason reason, string? tenantId, string? modelId)
    {
        var now = _time.GetUtcNow();
        _byReason.Add(reason.ToLabel(), now);
        if (!string.IsNullOrEmpty(tenantId))
        {
            _byTenant.Add(tenantId, now);
        }

        if (!string.IsNullOrEmpty(modelId))
        {
            _byModel.Add(modelId, now);
        }
    }

    public void RecordUnknownModel(string requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return;
        }

        var now = _time.GetUtcNow();
        _unknownModels.Add(requestedModel.Trim(), now);
        _byReason.Add(RejectionReason.ModelNotFound.ToLabel(), now);
    }

    public void RecordGrantDenial(string? tenantId, string modelId)
    {
        var now = _time.GetUtcNow();
        _grantDenials.Add((tenantId ?? "?") + "|" + modelId, now);
        RecordRejection(RejectionReason.GrantDenied, tenantId, modelId);
    }

    public void RecordBudgetRejection(string? tenantId, string? budgetName, string modelId)
    {
        var now = _time.GetUtcNow();
        _budgets.Add(string.IsNullOrEmpty(budgetName) ? "(unnamed)" : budgetName, now);
        RecordRejection(RejectionReason.Budget, tenantId, modelId);
    }

    public PolicyLiveOverview Snapshot(int take = 10)
    {
        var now = _time.GetUtcNow();
        return new PolicyLiveOverview
        {
            RejectionsByReason1h = _byReason.Top(now, 60, take),
            RejectionsByReason24h = _byReason.Top(now, 1440, take),
            RejectionsByTenant1h = _byTenant.Top(now, 60, take),
            RejectionsByModel1h = _byModel.Top(now, 60, take),
            UnknownModels1h = _unknownModels.Top(now, 60, take),
            GrantDenials1h = _grantDenials.Top(now, 60, take),
            BudgetRejections1h = _budgets.Top(now, 60, take),
        };
    }

    public IReadOnlyList<CountRow> GrantDenials(int minutes, int take = 20) => _grantDenials.Top(_time.GetUtcNow(), minutes, take);

    public IReadOnlyList<CountRow> UnknownModels(int minutes, int take = 20) => _unknownModels.Top(_time.GetUtcNow(), minutes, take);

    public void Reset()
    {
        foreach (var d in new[] { _byReason, _byTenant, _byModel, _unknownModels, _grantDenials, _budgets })
        {
            d.Clear();
        }
    }
}
