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
    const string source = """
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
    const string source = """
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
    const string source = """
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
    const string source = """
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
    const string source = """
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
    const string source = """
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
    const string source = """
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
    const string source = """
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
    const string source = """
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

  // ========================================
  // WHIZ106: Collective apply must not do apply-time cross-perspective queries
  // (ICollectiveQuery.Of<>()) — such a spec cannot be re-evaluated per-row during
  // a single-stream replay/rebuild, so it is non-replayable. Cross-queries must be
  // resolved BEFORE event creation and baked into the event as static payload.
  // ========================================

  /// <summary>
  /// A [CollectiveApplyFor] method that invokes ICollectiveQuery.Of&lt;&gt;() performs an apply-time
  /// cross-perspective query and must report WHIZ106 (non-replayable).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CollectiveApplyUsesCollectiveQuery_ReportsWHIZ106Async() {
    // Arrange — handler whose Where correlates against a sibling perspective via q.Of<>()
    const string source = """
            using System;
            using System.Linq;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OverlayModel {
              public Guid Id { get; set; }
              public Guid GlobalTemplateId { get; set; }
              public bool IsActive { get; set; }
            }

            public class OverlayAppliedEvent {
              public Guid OverlayId { get; set; }
              public Guid GlobalTemplateId { get; set; }
            }

            public class OverlayHandler {
              [CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Custom)]
              public ICollectiveSpec<OverlayModel> ClearSiblings(OverlayAppliedEvent e, ICollectiveQuery query) {
                var exists = query.Of<OverlayModel>().Any(o => o.Data.GlobalTemplateId == e.GlobalTemplateId);
                return null!;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz106 = diagnostics.Where(d => d.Id == "WHIZ106").ToArray();
    await Assert.That(whiz106.Length).IsEqualTo(1)
      .Because("q.Of<>() is an apply-time cross-perspective query — non-replayable per-row");
    await Assert.That(whiz106[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz106[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ClearSiblings");
  }

  /// <summary>
  /// A self-referential [CollectiveApplyFor] method — takes the ICollectiveQuery parameter but never
  /// invokes it (Where references only the row's own columns) — is replay-safe and must NOT report WHIZ106.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_SelfReferentialCollectiveApply_NoWHIZ106Async() {
    // Arrange — the single-active flip: Where on the row's own GlobalTemplateId, Set IsActive=(Id==e.OverlayId).
    // Takes the query param (signature parity) but never touches it.
    const string source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OverlayModel {
              public Guid Id { get; set; }
              public Guid GlobalTemplateId { get; set; }
              public bool IsActive { get; set; }
            }

            public class OverlayAppliedEvent {
              public Guid OverlayId { get; set; }
              public Guid GlobalTemplateId { get; set; }
            }

            public class OverlayHandler {
              [CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Custom)]
              public ICollectiveSpec<OverlayModel> SetActive(OverlayAppliedEvent e, ICollectiveQuery query) {
                return null!;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz106 = diagnostics.Where(d => d.Id == "WHIZ106").ToArray();
    await Assert.That(whiz106).IsEmpty();
  }

  /// <summary>
  /// A non-collective method that happens to use ICollectiveQuery.Of&lt;&gt;() is not a collective apply
  /// (no [CollectiveApplyFor]) and must NOT report WHIZ106 — the rule targets replayable collective specs only.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_NonCollectiveMethodUsesQuery_NoWHIZ106Async() {
    // Arrange
    const string source = """
            using System;
            using System.Linq;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OverlayModel {
              public Guid Id { get; set; }
              public Guid GlobalTemplateId { get; set; }
            }

            public class SomeService {
              public bool AnySiblings(ICollectiveQuery query, Guid family) {
                return query.Of<OverlayModel>().Any(o => o.Data.GlobalTemplateId == family);
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz106 = diagnostics.Where(d => d.Id == "WHIZ106").ToArray();
    await Assert.That(whiz106).IsEmpty();
  }

  /// <summary>
  /// Collective applies must be deterministic too — a [CollectiveApplyFor] method that reads DateTime.UtcNow
  /// reports WHIZ104 (same determinism rule as a perspective Apply). This is what makes replay reproducible.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_CollectiveApplyUsesDateTimeUtcNow_ReportsWHIZ104Async() {
    // Arrange
    const string source = """
            using System;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public class OverlayModel {
              public Guid Id { get; set; }
              public DateTimeOffset TouchedAt { get; set; }
            }

            public class OverlayAppliedEvent {
              public Guid OverlayId { get; set; }
            }

            public class OverlayHandler {
              [CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Custom)]
              public ICollectiveSpec<OverlayModel> Touch(OverlayAppliedEvent e, ICollectiveQuery query) {
                var now = DateTime.UtcNow;
                return null!;
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert
    var whiz104 = diagnostics.Where(d => d.Id == "WHIZ104").ToArray();
    await Assert.That(whiz104.Length).IsEqualTo(1)
      .Because("collective applies are folded during replay and must be deterministic — no DateTime.UtcNow");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Analyzer_IPerspectiveWithActionsFor_DetectsImpureApplyAsync() {
    // Arrange — IPerspectiveWithActionsFor with impure Apply (uses DateTime.UtcNow)
    // Matches format of existing purity tests
    const string source = """
            using System;
            using Whizbang.Core;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public record DeletedEvent : IEvent {
              [StreamId]
              public Guid Id { get; init; }
            }

            public record OrderModel {
              [StreamId]
              public Guid Id { get; init; }
              public DateTimeOffset DeletedAt { get; init; }
            }

            public class ImpureWithActionsPerspective : IPerspectiveWithActionsFor<OrderModel, DeletedEvent> {
              public ApplyResult<OrderModel> Apply(OrderModel current, DeletedEvent @event) {
                return new OrderModel { Id = current.Id, DeletedAt = DateTime.UtcNow };
              }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectivePurityAnalyzer>(source);

    // Assert — Purity analyzer must detect DateTime.UtcNow in WithActionsFor Apply methods (WHIZ104)
    var whiz104 = diagnostics.Where(d => d.Id == "WHIZ104").ToArray();
    await Assert.That(whiz104.Length).IsGreaterThanOrEqualTo(1)
      .Because("IPerspectiveWithActionsFor Apply methods must be checked for purity — DateTime.UtcNow is impure");
  }
}
