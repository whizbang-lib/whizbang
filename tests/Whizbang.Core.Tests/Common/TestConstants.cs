namespace Whizbang.Core.Tests.Common;

/// <summary>
/// Shared constants for test assertions.
/// Update these when intentionally adding/removing test receptors.
/// </summary>
public static class TestConstants {
  /// <summary>
  /// Expected total receptor count across all test assemblies.
  ///
  /// Breakdown by source:
  /// - 3 receptors from DispatcherTests.cs (DispatcherTestOrderReceptor, LogReceptor, ProcessReceptor)
  /// - 7 receptors from VoidReceptorExamples.cs (LogUserActionReceptor, SendNotificationReceptor,
  ///     UpdateCacheReceptor, ProcessPaymentReceptor, AuditOrderReceptor, AnalyticsOrderReceptor, EmailOrderReceptor)
  /// - 5 receptors from MultiReceptorTests.cs (OrderReceptor, ShippingReceptor, PaymentReceptor,
  ///     UserReceptor, EmailReceptor)
  /// - 5 receptors from TupleReturnTests.cs (OrderReceptor, OrderBusinessReceptor, OrderAuditReceptor,
  ///     PaymentReceptor, NotificationReceptor)
  /// - 3 receptors from ExecutionTests.cs (ProcessPaymentReceptor, SendEmailReceptor, LogEventReceptor)
  /// - 5 additional receptors from other test files
  ///
  /// Total: 28 receptors
  /// </summary>
  public const int EXPECTED_RECEPTOR_COUNT = 28;
}
