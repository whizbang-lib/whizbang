using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for PerspectivePurityAnalyzer WHIZ100-105.
/// Verifies detection of impure patterns in perspective Apply methods and constructors.
/// </summary>
/// <tests>Whizbang.Generators/PerspectivePurityAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectivePurityAnalyzerTests {

  // ========================================
  // WHIZ105: Non-Pure Service Injection Tests
  // ========================================

  /// <summary>
  /// Test that injecting a non-pure service reports WHIZ105 warning.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NonPureServiceInjection_ReportsWHIZ105WarningAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            // Service without [PureService] attribute
            public interface IOrderService {
              void ProcessOrder();
            }

            public class Order {
              public Guid Id { get; set; }
              public string Status { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
              public string NewStatus { get; set; }
            }

            // Perspective injecting non-pure service
            public class OrderPerspective : IPerspectiveFor<Order, OrderUpdated> {
              private readonly IOrderService _orderService;

              public OrderPerspective(IOrderService orderService) {
                _orderService = orderService;
              }

              public Order Apply(Order current, OrderUpdated @event) {
                return current with { Status = @event.NewStatus };
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz105 = diagnostics.Where(d => d.Id == "WHIZ105").ToArray();
    await Assert.That(whiz105.Length).IsEqualTo(1);
    await Assert.That(whiz105[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(whiz105[0].GetMessage(CultureInfo.InvariantCulture)).Contains("IOrderService");
  }

  /// <summary>
  /// Test that injecting a service marked with [PureService] does not report WHIZ105.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_PureServiceInjection_NoWarningAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            // Service marked with [PureService]
            [PureService]
            public interface IExchangeRateService {
              decimal GetRate(string currency, DateTimeOffset date);
            }

            public class Order {
              public Guid Id { get; set; }
              public decimal Total { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
              public string Currency { get; set; }
              public DateTimeOffset UpdatedAt { get; set; }
            }

            // Perspective injecting pure service
            public class OrderPerspective : IPerspectiveFor<Order, OrderUpdated> {
              private readonly IExchangeRateService _exchangeRates;

              public OrderPerspective(IExchangeRateService exchangeRates) {
                _exchangeRates = exchangeRates;
              }

              public Order Apply(Order current, OrderUpdated @event) {
                var rate = _exchangeRates.GetRate(@event.Currency, @event.UpdatedAt);
                return current with { Total = current.Total * rate };
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz105 = diagnostics.Where(d => d.Id == "WHIZ105").ToArray();
    await Assert.That(whiz105).IsEmpty();
  }

  /// <summary>
  /// Test that perspective with no constructor dependencies does not report WHIZ105.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NoDependencies_NoWarningAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class Order {
              public Guid Id { get; set; }
              public string Status { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
              public string NewStatus { get; set; }
            }

            // Perspective with no dependencies
            public class OrderPerspective : IPerspectiveFor<Order, OrderUpdated> {
              public Order Apply(Order current, OrderUpdated @event) {
                return current with { Status = @event.NewStatus };
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz105 = diagnostics.Where(d => d.Id == "WHIZ105").ToArray();
    await Assert.That(whiz105).IsEmpty();
  }

  /// <summary>
  /// Test that value types in constructor do not trigger WHIZ105.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ValueTypeParameter_NoWarningAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class Order {
              public Guid Id { get; set; }
              public decimal Multiplier { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
            }

            // Perspective with value type parameter (unusual but valid)
            public class OrderPerspective : IPerspectiveFor<Order, OrderUpdated> {
              private readonly decimal _multiplier;

              public OrderPerspective(decimal multiplier) {
                _multiplier = multiplier;
              }

              public Order Apply(Order current, OrderUpdated @event) {
                return current with { Multiplier = _multiplier };
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz105 = diagnostics.Where(d => d.Id == "WHIZ105").ToArray();
    await Assert.That(whiz105).IsEmpty();
  }

  /// <summary>
  /// Test that class implementing interface with [PureService] does not trigger WHIZ105.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ClassImplementingPureServiceInterface_NoWarningAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Attributes;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            [PureService]
            public interface ITaxCalculator {
              decimal Calculate(decimal amount);
            }

            public class TaxCalculator : ITaxCalculator {
              public decimal Calculate(decimal amount) => amount * 0.1m;
            }

            public class Order {
              public Guid Id { get; set; }
              public decimal Tax { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
              public decimal Amount { get; set; }
            }

            // Injecting the concrete class (which implements [PureService] interface)
            public class OrderPerspective : IPerspectiveFor<Order, OrderUpdated> {
              private readonly ITaxCalculator _calculator;

              public OrderPerspective(ITaxCalculator calculator) {
                _calculator = calculator;
              }

              public Order Apply(Order current, OrderUpdated @event) {
                return current with { Tax = _calculator.Calculate(@event.Amount) };
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz105 = diagnostics.Where(d => d.Id == "WHIZ105").ToArray();
    await Assert.That(whiz105).IsEmpty();
  }

  /// <summary>
  /// Test that non-perspective class with non-pure service does not trigger WHIZ105.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NonPerspectiveClass_NoWarningAsync() {
    // Arrange
    var source = """
            using System;

            namespace TestApp;

            public interface IOrderService {
              void ProcessOrder();
            }

            // Regular service, not a perspective
            public class OrderHandler {
              private readonly IOrderService _orderService;

              public OrderHandler(IOrderService orderService) {
                _orderService = orderService;
              }

              public void Handle() {
                _orderService.ProcessOrder();
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz105 = diagnostics.Where(d => d.Id == "WHIZ105").ToArray();
    await Assert.That(whiz105).IsEmpty();
  }

  /// <summary>
  /// Test that IGlobalPerspectiveFor also triggers WHIZ105 for non-pure services.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_GlobalPerspectiveWithNonPureService_ReportsWHIZ105Async() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public interface INotificationService {
              void SendNotification(string message);
            }

            public class AuditLog {
              public Guid Id { get; set; }
              public string Message { get; set; }
            }

            public class AuditEvent {
              public string Details { get; set; }
            }

            // Global perspective with non-pure dependency
            public class AuditPerspective : IGlobalPerspectiveFor<AuditLog, Guid, AuditEvent> {
              private readonly INotificationService _notifications;

              public AuditPerspective(INotificationService notifications) {
                _notifications = notifications;
              }

              public AuditLog Apply(AuditLog current, AuditEvent @event) {
                return current with { Message = @event.Details };
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz105 = diagnostics.Where(d => d.Id == "WHIZ105").ToArray();
    await Assert.That(whiz105.Length).IsEqualTo(1);
    await Assert.That(whiz105[0].GetMessage(CultureInfo.InvariantCulture)).Contains("INotificationService");
  }

  // ========================================
  // WHIZ100: Async Apply Method Tests
  // ========================================

  /// <summary>
  /// Test that async Apply method reports WHIZ100 error.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_AsyncApplyMethod_ReportsWHIZ100ErrorAsync() {
    // Arrange
    var source = """
            using System;
            using System.Threading.Tasks;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class Order {
              public Guid Id { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
            }

            public class OrderPerspective : IPerspectiveFor<Order, OrderUpdated> {
              public async Task<Order> Apply(Order current, OrderUpdated @event) {
                await Task.Delay(1);
                return current;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz100 = diagnostics.Where(d => d.Id == "WHIZ100").ToArray();
    await Assert.That(whiz100.Length).IsEqualTo(1);
    await Assert.That(whiz100[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  // ========================================
  // WHIZ104: DateTime.UtcNow Usage Tests
  // ========================================

  /// <summary>
  /// Test that DateTime.UtcNow usage reports WHIZ104 warning.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_DateTimeUtcNowUsage_ReportsWHIZ104WarningAsync() {
    // Arrange
    var source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class Order {
              public Guid Id { get; set; }
              public DateTimeOffset UpdatedAt { get; set; }
            }

            public class OrderUpdated {
              public Guid OrderId { get; set; }
            }

            public class OrderPerspective : IPerspectiveFor<Order, OrderUpdated> {
              public Order Apply(Order current, OrderUpdated @event) {
                return current with { UpdatedAt = DateTime.UtcNow };
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz104 = diagnostics.Where(d => d.Id == "WHIZ104").ToArray();
    await Assert.That(whiz104.Length).IsEqualTo(1);
    await Assert.That(whiz104[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }
}
