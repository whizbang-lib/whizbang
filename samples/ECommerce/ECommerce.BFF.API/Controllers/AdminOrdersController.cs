using ECommerce.BFF.API.Lenses;
using Microsoft.AspNetCore.Mvc;

namespace ECommerce.BFF.API.Controllers;

/// <summary>
/// Admin-facing order API endpoints for tenant administrators.
/// Provides visibility into all orders for a specific tenant.
/// </summary>
[ApiController]
[Route("api/admin/orders")]
public class AdminOrdersController : ControllerBase {
  private readonly IOrderLens _orderLens;
  private readonly ILogger<AdminOrdersController> _logger;

  public AdminOrdersController(
    IOrderLens orderLens,
    ILogger<AdminOrdersController> _logger
  ) {
    _orderLens = orderLens;
    this._logger = _logger;
  }

  /// <summary>
  /// Get all orders for the current tenant
  /// </summary>
  [HttpGet]
  public async Task<ActionResult<IEnumerable<OrderReadModel>>> GetAll([FromQuery] string tenantId) {
    // TODO: Get tenantId from SecurityContext/JWT claims instead of query param
    // TODO: Verify user is admin for this tenant
    if (string.IsNullOrEmpty(tenantId)) {
      return BadRequest("TenantId is required");
    }

    var orders = await _orderLens.GetByTenantIdAsync(tenantId);
    return Ok(orders);
  }

  /// <summary>
  /// Get orders by status for the current tenant
  /// </summary>
  [HttpGet("status/{status}")]
  public async Task<ActionResult<IEnumerable<OrderReadModel>>> GetByStatus(
    string status,
    [FromQuery] string tenantId
  ) {
    // TODO: Get tenantId from SecurityContext/JWT claims
    // TODO: Verify user is admin for this tenant
    if (string.IsNullOrEmpty(tenantId)) {
      return BadRequest("TenantId is required");
    }

    var orders = await _orderLens.GetByStatusAsync(tenantId, status);
    return Ok(orders);
  }

  /// <summary>
  /// Get a specific order by ID (admin view)
  /// </summary>
  [HttpGet("{orderId}")]
  public async Task<ActionResult<OrderReadModel>> GetById(
    string orderId,
    [FromQuery] string tenantId
  ) {
    // TODO: Get tenantId from SecurityContext/JWT claims
    // TODO: Verify user is admin for this tenant
    if (string.IsNullOrEmpty(tenantId)) {
      return BadRequest("TenantId is required");
    }

    var order = await _orderLens.GetByIdAsync(orderId);

    if (order == null) {
      return NotFound();
    }

    // Verify order belongs to this tenant
    if (order.TenantId != tenantId) {
      return Forbid();
    }

    return Ok(order);
  }
}
