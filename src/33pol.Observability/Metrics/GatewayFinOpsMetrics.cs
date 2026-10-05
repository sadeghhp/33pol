using System.Diagnostics.Metrics;
using Pol33.Core.Billing;
using Pol33.Core.Models.Overview;

namespace Pol33.Observability.Metrics;

/// <summary>One API key's month-to-date cost, already resolved to a tenant slug. No key prefix.</summary>
public sealed record FinOpsKeyCost(string TenantSlug, Guid KeyId, string? Label, string? Assignee, decimal Cost);

/// <summary>
/// Publishes the Overview's FinOps and tenant snapshots as Prometheus gauges, and is the place the
/// billed-cost counter's label rules live. Gauges are levels (use <c>max</c> across replicas, which
/// each export the same database snapshot). The cost counter is incremented only for events newly
/// inserted into the ledger.
/// </summary>
public static class GatewayFinOpsMetrics
{
    private static readonly object Gate = new();
    private static FinOpsMetricSnapshot _current = FinOpsMetricSnapshot.Absent;

    public static FinOpsMetricSnapshot Current
    {
        get
        {
            lock (Gate)
            {
                return _current;
            }
        }
    }

    public static void Publish(FinOpsMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (Gate)
        {
            _current = snapshot;
        }
    }

    public static FinOpsMetricSnapshot Build(
        FinOpsOverview? finops,
        PolicyOverview? policy,
        TenantsOverview? tenants,
        IReadOnlyList<FinOpsKeyCost> keyCosts)
    {
        if (finops is null && policy is null && tenants is null)
        {
            return FinOpsMetricSnapshot.Absent;
        }

        return new FinOpsMetricSnapshot
        {
            HasFinOps = finops is not null,
            HasPolicy = policy is not null,
            HasTenants = tenants is not null,
            TodayCost = (double)(finops?.TodayCost ?? 0m),
            MonthToDateCost = (double)(finops?.MonthToDateCost ?? 0m),
            UnpricedModels = finops?.UnpricedModelIds.Count ?? 0,
            AnonymousRequestShare = tenants?.AnonymousRequestShare ?? 0d,
            ActiveKeys = tenants?.KeyCount ?? 0,
            ExpiringKeys = tenants?.ExpiringKeys.Count ?? 0,
            IdleKeys = tenants?.IdleKeys.Count ?? 0,
            Budgets = finops?.Budgets
                .Where(b => b.Limit > 0)
                .Select(b => new BudgetSample(
                    TenantLabel(b.TenantSlug, b.TenantId),
                    string.IsNullOrWhiteSpace(b.Name) ? "(unnamed)" : b.Name,
                    b.Ratio,
                    b.HardStopEnabled))
                .ToArray() ?? [],
            Quotas = policy?.Quotas
                .Where(q => q.Limit > 0 && !string.IsNullOrWhiteSpace(q.TenantSlug))
                .Select(q => new QuotaSample(
                    q.TenantSlug!,
                    string.IsNullOrWhiteSpace(q.Period) ? "current" : q.Period,
                    q.Ratio))
                .ToArray() ?? [],
            TenantCosts = tenants?.TopConsumersMonthToDate
                .Select(c => new TenantCostSample(TenantLabel(c.TenantSlug, c.TenantId), (double)c.Cost))
                .ToArray() ?? [],
            Keys = SelectTopKeys(keyCosts),
        };
    }

    public static IEnumerable<Measurement<double>> ObserveSpend() => ObserveSpend(Current);

    public static IEnumerable<Measurement<double>> ObserveSpend(FinOpsMetricSnapshot snapshot)
    {
        if (!snapshot.HasFinOps)
        {
            yield break;
        }

        yield return new Measurement<double>(snapshot.TodayCost, new KeyValuePair<string, object?>("window", "today"));
        yield return new Measurement<double>(snapshot.MonthToDateCost, new KeyValuePair<string, object?>("window", "mtd"));
    }

    public static IEnumerable<Measurement<int>> ObserveUnpriced() => ObserveUnpriced(Current);

    public static IEnumerable<Measurement<int>> ObserveUnpriced(FinOpsMetricSnapshot snapshot)
    {
        if (!snapshot.HasFinOps)
        {
            yield break;
        }

        yield return new Measurement<int>(snapshot.UnpricedModels);
    }

    public static IEnumerable<Measurement<double>> ObserveAnonymousShare() => ObserveAnonymousShare(Current);

    public static IEnumerable<Measurement<double>> ObserveAnonymousShare(FinOpsMetricSnapshot snapshot)
    {
        if (!snapshot.HasTenants)
        {
            yield break;
        }

        yield return new Measurement<double>(snapshot.AnonymousRequestShare);
    }

    public static IEnumerable<Measurement<int>> ObserveKeyCounts() => ObserveKeyCounts(Current);

    public static IEnumerable<Measurement<int>> ObserveKeyCounts(FinOpsMetricSnapshot snapshot)
    {
        if (!snapshot.HasTenants)
        {
            yield break;
        }

        yield return new Measurement<int>(snapshot.ActiveKeys, new KeyValuePair<string, object?>("state", "active"));
        yield return new Measurement<int>(snapshot.ExpiringKeys, new KeyValuePair<string, object?>("state", "expiring"));
        yield return new Measurement<int>(snapshot.IdleKeys, new KeyValuePair<string, object?>("state", "idle"));
    }

    public static IEnumerable<Measurement<double>> ObserveBudgets() => ObserveBudgets(Current);

    public static IEnumerable<Measurement<double>> ObserveBudgets(FinOpsMetricSnapshot snapshot)
    {
        foreach (var budget in snapshot.Budgets)
        {
            yield return new Measurement<double>(
                budget.Ratio,
                new KeyValuePair<string, object?>("tenant", budget.Tenant),
                new KeyValuePair<string, object?>("budget", budget.Name),
                new KeyValuePair<string, object?>("hard_stop", budget.HardStop ? "true" : "false"));
        }
    }

    public static IEnumerable<Measurement<double>> ObserveQuotas() => ObserveQuotas(Current);

    public static IEnumerable<Measurement<double>> ObserveQuotas(FinOpsMetricSnapshot snapshot)
    {
        foreach (var quota in snapshot.Quotas)
        {
            yield return new Measurement<double>(
                quota.Ratio,
                new KeyValuePair<string, object?>("tenant", quota.Tenant),
                new KeyValuePair<string, object?>("period", quota.Period));
        }
    }

    public static IEnumerable<Measurement<double>> ObserveTenantCosts() => ObserveTenantCosts(Current);

    public static IEnumerable<Measurement<double>> ObserveTenantCosts(FinOpsMetricSnapshot snapshot)
    {
        foreach (var tenant in snapshot.TenantCosts)
        {
            yield return new Measurement<double>(
                tenant.Cost,
                new KeyValuePair<string, object?>("tenant", tenant.Tenant));
        }
    }

    public static IEnumerable<Measurement<double>> ObserveKeyCosts() => ObserveKeyCosts(Current);

    public static IEnumerable<Measurement<double>> ObserveKeyCosts(FinOpsMetricSnapshot snapshot)
    {
        foreach (var key in snapshot.Keys)
        {
            yield return new Measurement<double>(
                key.Cost,
                new KeyValuePair<string, object?>("tenant", key.Tenant),
                new KeyValuePair<string, object?>("key_label", key.Label),
                new KeyValuePair<string, object?>("assignee", key.Assignee),
                new KeyValuePair<string, object?>("key_id", key.KeyId));
        }
    }

    internal static string TenantLabel(string? slug, Guid? tenantId)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return slug;
        }

        return tenantId is null ? BillingMetricLabels.AnonymousTenant : BillingMetricLabels.UnknownTenant;
    }

    private static KeyCostSample[] SelectTopKeys(IReadOnlyList<FinOpsKeyCost> keyCosts)
    {
        if (keyCosts.Count == 0)
        {
            return [];
        }

        return keyCosts
            .Where(k => k.KeyId != Guid.Empty)
            .OrderByDescending(k => k.Cost)
            .ThenBy(k => k.KeyId)
            .Take(BillingMetricLabels.TopKeyLimit)
            .Select(k => new KeyCostSample(
                string.IsNullOrWhiteSpace(k.TenantSlug) ? BillingMetricLabels.UnknownTenant : k.TenantSlug,
                k.KeyId.ToString("D"),
                string.IsNullOrWhiteSpace(k.Label) ? BillingMetricLabels.UnlabeledKey : k.Label,
                string.IsNullOrWhiteSpace(k.Assignee) ? BillingMetricLabels.NoAssignee : k.Assignee,
                (double)k.Cost))
            .ToArray();
    }
}

public sealed class FinOpsMetricSnapshot
{
    public static FinOpsMetricSnapshot Absent { get; } = new();

    public bool HasFinOps { get; init; }

    public bool HasPolicy { get; init; }

    public bool HasTenants { get; init; }

    public double TodayCost { get; init; }

    public double MonthToDateCost { get; init; }

    public int UnpricedModels { get; init; }

    public double AnonymousRequestShare { get; init; }

    public int ActiveKeys { get; init; }

    public int ExpiringKeys { get; init; }

    public int IdleKeys { get; init; }

    public IReadOnlyList<BudgetSample> Budgets { get; init; } = [];

    public IReadOnlyList<QuotaSample> Quotas { get; init; } = [];

    public IReadOnlyList<TenantCostSample> TenantCosts { get; init; } = [];

    public IReadOnlyList<KeyCostSample> Keys { get; init; } = [];
}

public sealed record BudgetSample(string Tenant, string Name, double Ratio, bool HardStop);

public sealed record QuotaSample(string Tenant, string Period, double Ratio);

public sealed record TenantCostSample(string Tenant, double Cost);

public sealed record KeyCostSample(string Tenant, string KeyId, string Label, string Assignee, double Cost);
