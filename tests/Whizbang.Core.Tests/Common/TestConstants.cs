namespace Whizbang.Core.Tests.Common;

/// <summary>
/// Shared constants for test assertions.
/// Update these when intentionally adding/removing test receptors.
/// </summary>
public static class TestConstants {
  /// <summary>
  /// <para>Expected total receptor count across all test assemblies.</para>
  ///
  /// <para>Breakdown by source:
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
  ///     InheritedStreamIdCommandReceptor, InheritedOnlyIfEmptyCommandReceptor)</para>
  ///
  /// <para>- 7 receptors from DispatcherNewCodeCoverageTests.cs (SyncOnlyCommandReceptor, PropagateStreamIdCommandReceptor,
  ///     PropagatedStreamIdEventTrackerReceptor, VoidOptionsCommandReceptor, VoidOptionsEventTrackerReceptor,
  ///     SyncOptionsCommandReceptor, EmptyStreamIdEventReceptor)
  /// - 2 receptors from DispatcherInvokeWithReceiptTests.cs (ReceiptTestCommandReceptor, ReceiptTestVoidCommandReceptor)</para>
  ///
  /// <para>- 3 receptors from DispatcherOwnedDomainTests.cs + DispatcherCascadeFireCountTests.cs
  ///     (CascadeTestCommandHandler, FireCountCommandHandler, FireCountEventReceptor)
  /// - 3 receptors from DispatcherStageFireTests.cs
  ///     (StageTestCommandHandler, DefaultStageTestReceptor, ExplicitPostAllPerspectivesReceptor)
  /// - 2 receptors from DispatcherOwnedEventSelfEchoTests.cs
  ///     (SelfEchoCommandHandler, SelfEchoEventReceptor)</para>
  ///
  /// <para>- 2 receptors from DispatcherConcurrentOutboxTests.cs
  ///     (BlockingTestEventReceptor, ThrowingTestEventReceptor)</para>
  ///
  /// <para>- 1 receptor from DispatcherCascadeFlushTests.cs
  ///     (CascadeFlushCommandHandler — pins that cascade uses fire-and-forget FlushAsync)</para>
  ///
  /// <para>- 2 receptors from OrphanInboxJanitorExtensionsTests.cs
  ///     (FakeVoidReceptor, FakeResultReceptor — fixtures for janitor auto-wire test)</para>
  ///
  /// <para>- 1 receptor from DispatcherPublishOnceTests.cs
  ///     (PublishOnceTestEventReceptor — verifies winning the claim proceeds to PublishAsync,
  ///     which invokes the in-process receptor, in the saga-completion race fix lock-in)</para>
  ///
  /// <para>- 1 receptor from DispatcherScheduledForLocalReceptorTests.cs
  ///     (ScheduledForCascadeProbeReceptor — proves PublishAsync(event, options.ScheduledFor)
  ///     gates local-receptor invocation on scheduled time, fixing the production saga watchdog
  ///     cascade-abandon)</para>
  ///
  /// <para>Total: 119 receptors (includes coverage test types that implement ICommand/IEvent)</para>
  /// </summary>
  public const int EXPECTED_RECEPTOR_COUNT = 119;
}
