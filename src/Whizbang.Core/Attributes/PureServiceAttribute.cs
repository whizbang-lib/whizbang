namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks a service as pure and safe for injection into perspectives.
/// Pure services must be deterministic and have no side effects.
/// </summary>
/// <remarks>
/// <para>
/// Perspectives require deterministic behavior for event replay. Services injected
/// into perspectives must be pure - same inputs always produce same outputs.
/// </para>
/// <para>
/// The <c>PerspectivePurityAnalyzer</c> emits WHIZ105 warnings when a perspective
/// constructor injects a service that is not marked with <c>[PureService]</c>.
/// Developers can suppress the warning if they're certain the service is pure.
/// </para>
/// <para>
/// <b>Allowed Pure Service Patterns:</b>
/// <list type="bullet">
///   <item><b>Read-only lookups</b>: Stateless services (e.g., exchange rate lookups)</item>
///   <item><b>Pure computations</b>: Services that compute values from inputs only</item>
///   <item><b>Cached/immutable data</b>: Services backed by immutable reference data</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Mark a service interface as pure
/// [PureService(Reason = "Read-only exchange rate lookup")]
/// public interface IExchangeRateService {
///   decimal GetRate(string currency, DateTimeOffset date);
/// }
///
/// // Mark a service class as pure
/// [PureService]
/// public class TaxCalculator : ITaxCalculator {
///   public decimal CalculateTax(decimal amount, string region) {
///     // Pure calculation - no I/O, no state mutation
///     return amount * GetTaxRate(region);
///   }
/// }
///
/// // Inject into perspective (no WHIZ105 warning)
/// public class OrderPerspective : IPerspectiveFor&lt;Order, OrderShipped&gt; {
///   private readonly IExchangeRateService _rates;
///
///   public OrderPerspective(IExchangeRateService rates) {
///     _rates = rates;
///   }
///
///   public Order Apply(Order current, OrderShipped @event) {
///     var rate = _rates.GetRate(@event.Currency, @event.ShippedAt);
///     return current with { TotalInUsd = current.Total * rate };
///   }
/// }
/// </code>
/// </example>
/// <docs>core-concepts/perspectives#pure-services</docs>
/// <tests>Whizbang.Core.Tests/Attributes/PureServiceAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = true)]
public sealed class PureServiceAttribute : Attribute {
  /// <summary>
  /// Optional reason documenting why this service is considered pure.
  /// Helps maintainers understand the purity contract.
  /// </summary>
  /// <example>
  /// <code>
  /// [PureService(Reason = "Stateless calculation using only input values")]
  /// public class DiscountCalculator { }
  /// </code>
  /// </example>
  public string? Reason { get; init; }
}
