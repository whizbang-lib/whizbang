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
  /// - 4 receptors from LifecycleContextTests/FireAtAttributeTests/LifecycleStageIsolationTests/LifecycleReceptorRegistryTests
  ///     (TestReceptorWithContext, TestReceptorWithFireAt, TestReceptorWithMultipleFireAt, InvocationTrackingReceptor,
  ///     TestReceptor, AnotherTestReceptor)
  /// - 2 receptors from DispatcherOptionsAndRoutingTests.cs (TestCommandReceptor, TestCommandVoidReceptor)
  /// - 2 receptors from DispatcherLocalInvokeAndSyncTests.cs (CreateOrderReceptor, VoidCommandReceptor)
  /// - 2 receptors from DispatcherLocalInvokeAndSyncCallbackTests.cs (CallbackTestCommandReceptor, CallbackTestCommandWithResultReceptor)
  /// - 2 receptors from DispatcherLocalInvokeAndSyncTimingTests.cs (TimedCommandReceptor, TimedCommandWithResultReceptor)
  /// - 6 receptors from new test files added during cascade security context implementation
  /// - 3 receptors added during ScopeDelta/unified scope propagation changes
  /// - 6 receptors from DispatcherStreamIdGenerationTests.cs (GenerateStreamIdCommandReceptor,
  ///     GenerateStreamIdOnlyIfEmptyCommandReceptor, NoGenerateStreamIdCommandReceptor, SimpleCommandReceptor,
  ///     InheritedStreamIdCommandReceptor, InheritedOnlyIfEmptyCommandReceptor)
  ///
  /// - 7 receptors from DispatcherNewCodeCoverageTests.cs (SyncOnlyCommandReceptor, PropagateStreamIdCommandReceptor,
  ///     PropagatedStreamIdEventTrackerReceptor, VoidOptionsCommandReceptor, VoidOptionsEventTrackerReceptor,
  ///     SyncOptionsCommandReceptor, EmptyStreamIdEventReceptor)
  /// - 2 receptors from DispatcherInvokeWithReceiptTests.cs (ReceiptTestCommandReceptor, ReceiptTestVoidCommandReceptor)
  ///
  /// - 3 receptors from DispatcherOwnedDomainTests.cs + DispatcherCascadeFireCountTests.cs
  ///     (CascadeTestCommandHandler, FireCountCommandHandler, FireCountEventReceptor)
  /// - 3 receptors from DispatcherStageFireTests.cs
  ///     (StageTestCommandHandler, DefaultStageTestReceptor, ExplicitPostAllPerspectivesReceptor)
  ///
  /// Total: 110 receptors (includes coverage test types that implement ICommand/IEvent)
  /// </summary>
  public const int EXPECTED_RECEPTOR_COUNT = 110;
}
