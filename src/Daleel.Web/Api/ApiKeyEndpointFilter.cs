namespace Daleel.Web.Api;

/// <summary>
/// Minimal-API endpoint filter that runs the whole B2B gate (<see cref="IApiKeyAuthService"/>)
/// before the handler: bearer-key auth, revocation/suspension, scope check, credit balance + debit.
/// One instance per endpoint carries that endpoint's scope + charge; the auth service itself is
/// resolved from the REQUEST's service scope so it gets its own transient DbContext (the Blazor
/// circuit-safety rule applies to HTTP requests too — never share a context across concurrent work).
/// </summary>
public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    private readonly string _scope;
    private readonly string _endpointName;
    private readonly string _pricingKey;
    private readonly int _defaultCharge;

    public ApiKeyEndpointFilter(string scope, string endpointName, string pricingKey, int defaultCharge)
    {
        _scope = scope;
        _endpointName = endpointName;
        _pricingKey = pricingKey;
        _defaultCharge = defaultCharge;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var auth = http.RequestServices.GetRequiredService<IApiKeyAuthService>();
        var result = await auth.AuthenticateAndChargeAsync(
            http.Request.Headers.Authorization.ToString(),
            _scope, _endpointName, _pricingKey, _defaultCharge, http.RequestAborted);

        if (!result.Succeeded)
        {
            return Results.Json(new { error = result.ErrorCode }, statusCode: result.ErrorStatus!.Value);
        }

        // Stamp the resolved application for the handler / any later metering enrichment.
        http.Items[ApplicationItemKey] = result.Application;
        return await next(context);
    }

    /// <summary>HttpContext.Items key under which the authenticated <c>ApiApplication</c> is stored.</summary>
    public const string ApplicationItemKey = "Daleel.B2b.Application";
}
