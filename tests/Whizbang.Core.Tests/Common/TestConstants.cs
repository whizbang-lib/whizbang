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
  /// - 5 receptors from ReceptorTests.cs (OrderReceptor, OrderBusinessReceptor, OrderAuditReceptor,
  ///     PaymentReceptor, NotificationReceptor)
  /// - 3 receptors from VoidReceptorTests.cs (ProcessPaymentReceptor, SendEmailReceptor, LogEventReceptor)
  /// - 9 receptors from DispatcherCascadeTests.cs (TupleReturningReceptor, ArrayReturningReceptor,
  ///     MultiEventTupleReceptor, NestedTupleReceptor, NonEventReturningReceptor, EmptyArrayReceptor,
  ///     EventTrackingReceptor, ShippedEventTrackingReceptor, NotificationEventTrackingReceptor)
  /// - 4 receptors from DispatcherSyncTests.cs (AsyncOrderReceptor, SyncOrderReceptor, SyncTupleReceptor, VoidSyncLogReceptor)
  /// - 3 receptors from SyncReceptorTests.cs (SyncOrderReceptor, SyncTupleReceptor, VoidSyncReceptor)
  /// - 2 receptors from DispatcherVoidCascadeTests.cs (ProcessOrderReceptor, OrderProcessedEventTracker)
  /// - 5 receptors from DispatcherRpcExtractionTests.cs (TupleReturningReceptor, MultiEventReceptor,
  ///     SimpleReceptor, InventoryReservedTracker, PaymentInitiatedTracker)
  /// - 3 receptors from DispatcherTests.cs (DispatcherTestOrderReceptor, LogReceptor, ProcessReceptor)
  /// - 2 receptors from DispatcherDeliveryReceiptTests.cs (CreateOrderReceptor, ProcessPaymentReceptor)
  /// - 1 receptor from DispatcherCascadeSecurityPropagationTests.cs (CascadeTestCommandReceptor)
  /// - 1 receptor from DispatcherSecurityPropagationTests.cs (SecurityPropagationTestCommandReceptor)
  /// - 2 receptors from DispatcherSecurityBuilderTests.cs (DispatcherSecurityBuilderTestCommandReceptor,
  ///     DispatcherSecurityBuilderVoidReceptor)
  /// - 2 receptors from DispatcherTagProcessingTests.cs (TestCommandReceptor, ThrowingReceptor)
  /// - 7 receptors from VoidReceptorExamples.cs (LogUserActionReceptor, SendNotificationReceptor,
  ///     UpdateCacheReceptor, ProcessPaymentReceptor, AuditOrderReceptor, AnalyticsOrderReceptor, EmailOrderReceptor)
  /// - 4 receptors from LifecycleContextTests/FireAtAttributeTests/LifecycleStageIsolationTests/LifecycleReceptorRegistryTests
  ///     (TestReceptorWithContext, TestReceptorWithFireAt, TestReceptorWithMultipleFireAt, InvocationTrackingReceptor,
  ///     TestReceptor, AnotherTestReceptor)
  /// - 2 receptors from DispatcherOptionsAndRoutingTests.cs (TestCommandReceptor, TestCommandVoidReceptor)
  ///
  /// Total: 59 receptors
  /// </summary>
  public const int EXPECTED_RECEPTOR_COUNT = 59;
}
