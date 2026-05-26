using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Middleware;

public sealed class QuotaMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IQuotaService _quotaService;
    private readonly IBudgetEnforcementService _budgetEnforcement;
    private readonly IErrorResponseWriter _errors;

    public QuotaMiddleware(
        RequestDelegate next,
        IQuotaService quotaService,
        IBudgetEnforcementService budgetEnforcement,
        IErrorResponseWriter errors)
    {
        _next = next;
        _quotaService = quotaService;
        _budgetEnforcement = budgetEnforcement;
        _errors = errors;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!InferenceRouteClassifier.IsRoutableInference(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var partitionKey = ResolvePartitionKey(context);
        var modelHint = context.Request.Query.TryGetValue("model", out var q) ? q.ToString() : string.Empty;

        var budgetCheck = await _budgetEnforcement
            .CheckBeforeForwardAsync(partitionKey == "anonymous" ? null : partitionKey, context.RequestAborted)
            .ConfigureAwait(false);
        if (!budgetCheck.IsAllowed)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.QuotaExceeded),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var check = _quotaService.CheckBeforeForward(partitionKey, modelHint);
        if (!check.IsAllowed)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.QuotaExceeded),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (check.IsSoftWarning && !string.IsNullOrEmpty(check.WarningMessage))
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[GatewayHeaders.QuotaWarning] = check.WarningMessage;
                return Task.CompletedTask;
            });
        }

        await _next(context).ConfigureAwait(false);
    }

    private static string ResolvePartitionKey(HttpContext context) =>
        context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var value) &&
        value is TenantContext tenant &&
        !string.IsNullOrWhiteSpace(tenant.TenantId)
            ? tenant.TenantId
            : "anonymous";
}
