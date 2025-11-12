using ECommerce.BFF.API.Lenses;
using Microsoft.AspNetCore.Mvc;

namespace ECommerce.BFF.API.Controllers;

/// <summary>
/// Super-admin facing order API endpoints.
/// Provides system-wide visibility across all tenants.
/// </summary>
[ApiController]
[Route("api/superadmin/orders")]
public class SuperAdminOrdersController : ControllerBase {
  private readonly IOrderLens _orderLens;
  private readonly ILogger<SuperAdminOrdersController> _logger;

  public SuperAdminOrdersController(
    IOrderLens orderLens,
    ILogger<SuperAdminOrdersController> logger
  ) {
    _orderLens = orderLens;
    _logger = logger;
  }

  /// <summary>
  /// Get recent orders across all tenants
  /// </summary>
  [HttpGet("recent")]
  public async Task<ActionResult<IEnumerable<OrderReadModel>>> GetRecentOrders([FromQuery] int limit = 100) {
    // TODO: Verify user is super-admin
    if (limit < 1 || limit > 1000) {
      return BadRequest("Limit must be between 1 and 1000");
    }

    var orders = await _orderLens.GetRecentOrdersAsync(limit);
    return Ok(orders);
  }

  /// <summary>
  /// Get all orders for a specific tenant (super-admin view)
  /// </summary>
  [HttpGet("tenant/{tenantId}")]
  public async Task<ActionResult<IEnumerable<OrderReadModel>>> GetByTenant(string tenantId) {
    // TODO: Verify user is super-admin
    var orders = await _orderLens.GetByTenantIdAsync(tenantId);
    return Ok(orders);
  }

  /// <summary>
  /// Get a specific order by ID (super-admin view, cross-tenant)
  /// </summary>
  [HttpGet("{orderId}")]
  public async Task<ActionResult<OrderReadModel>> GetById(string orderId) {
    // TODO: Verify user is super-admin
    var order = await _orderLens.GetByIdAsync(orderId);

    if (order == null) {
      return NotFound();
    }

    return Ok(order);
  }
}
