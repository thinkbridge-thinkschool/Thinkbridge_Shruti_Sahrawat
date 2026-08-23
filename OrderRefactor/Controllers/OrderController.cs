using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderRefactor.Authentication;
using OrderRefactor.Models;
using OrderRefactor.Services;

namespace OrderRefactor.Controllers;

[Authorize]
[ApiController]
[Route("api/orders")]
public class OrderController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrderController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<OrderResponse>> CreateOrder(
        [FromBody] CreateOrderRequest request,
        CancellationToken ct)
    {
        var response = await _orderService.CreateOrderAsync(request, ct);
        return CreatedAtAction(nameof(CreateOrder), new { id = response.OrderId }, response);
    }

    /// <summary>
    /// Reports who the caller is and which of the two bearer schemes validated them.
    /// </summary>
    /// <remarks>
    /// Authentication only — no policy. The point of this endpoint is to make the
    /// dual-scheme setup observable from outside: the same URL, called with a
    /// self-issued token and with a Microsoft-issued one, comes back authenticated
    /// under a different issuer each time.
    ///
    /// Without it the only endpoint here is CreateOrder, which requires the
    /// AdminOnly policy. An Entra token carries no `admin` claim, so it returns
    /// 403 — which does prove the token was validated, since an invalid token
    /// gives 401 — but proving authentication works by watching authorization
    /// deny you is a roundabout way to make the point.
    /// </remarks>
    [HttpGet("whoami")]
    public IActionResult WhoAmI()
    {
        var issuer = User.FindFirst("iss")?.Value
                     ?? User.Claims.FirstOrDefault()?.Issuer
                     ?? "unknown";

        var scheme = issuer.StartsWith(IssuerSchemeSelector.EntraIssuerPrefix, StringComparison.OrdinalIgnoreCase)
            ? IssuerSchemeSelector.EntraScheme
            : IssuerSchemeSelector.InternalScheme;

        return Ok(new
        {
            authenticated = User.Identity?.IsAuthenticated ?? false,
            issuer,
            validated_by = scheme,
            // Identity.Name is null for an Entra v2 token: it maps from the WS-Fed
            // name claim, which v2 does not issue. The self-issued tokens do carry
            // it, so both are checked before falling back to the OIDC claims.
            name = User.Identity?.Name
                   ?? User.FindFirst("name")?.Value
                   ?? User.FindFirst("preferred_username")?.Value,
            claims = User.Claims
                .Select(c => new { type = c.Type, value = c.Value })
                .ToArray()
        });
    }
}
