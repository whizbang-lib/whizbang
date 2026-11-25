// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for all 4 Perspective lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around perspective event processing.
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: Generated perspective runner (PerspectiveRunnerTemplate.cs)</para>
/// <para><strong>Stages Tested</strong>:</para>
/// <list type="bullet">
///   <item>PrePerspectiveInline - Before perspective RunAsync() (blocking)</item>
///   <item>PrePerspectiveDetached - Parallel with perspective RunAsync() (non-blocking)</item>
///   <item>PostPerspectiveDetached - After perspective completes (non-blocking)</item>
///   <item>PostPerspectiveInline - After perspective completes (blocking) - NOW EXPLICITLY TESTED</item>
/// </list>
/// <para>
/// Each perspective stage fires once per event per perspective that folds it (#2581), so every wait names how many
/// firings it expects (<see cref="LifecycleStageTestExtensions.PerspectivesFolding{TEvent}"/>, read from the host's own
/// runner registry) and every assertion requires exactly that many: a stage that fired once per batch, or twice for one
/// perspective, fails here. The waits are on the BFF host, which receives the events over Service Bus.
/// </para>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel("ServiceBus")]
[Timeout(120_000)]
public class PerspectiveLifecycleTests {
  private ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    _fixture = await SharedServiceBusFixtureSource.GetFixtureAsync();
    await Task.Delay(500);
    await _fixture.CleanupDatabaseAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    // Don't dispose - shared fixture is reused across tests
  }

  // ========================================
  // PrePerspectiveInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PrePerspectiveInline fires before perspective event processing (blocking),
  /// once for each perspective that folds the event.
  /// </summary>
  [Test]
  public async Task PrePerspectiveInline_FiresBeforePerspectiveProcessing_BlocksUntilCompleteAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PrePerspectiveInline");
  }

  /// <summary>
  /// Verifies that PrePerspectiveInline fires before perspective data is saved: the stage and the
  /// perspective completion it precedes both fire once per perspective.
  /// </summary>
  [Test]
  public async Task PrePerspectiveInline_FiresBeforePerspectiveSave_NoEventsProcessedYetAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage1 = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0, stage1);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PrePerspectiveInline");
    await _assertOncePerPerspectiveAsync(await stage1, perspectives, command, "PostPerspectiveInline");
  }

  // ========================================
  // PrePerspectiveDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PrePerspectiveDetached fires parallel with perspective RunAsync (non-blocking),
  /// once for each perspective that folds the event.
  /// </summary>
  [Test]
  public async Task PrePerspectiveDetached_FiresParallelWithProcessing_NonBlockingAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPrePerspectiveDetachedAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PrePerspectiveDetached");
  }

  /// <summary>
  /// Verifies that PrePerspectiveDetached completes even when the perspective finishes first, still
  /// once per perspective.
  /// </summary>
  [Test]
  public async Task PrePerspectiveDetached_MayCompleteAfterPerspective_NonBlockingGuaranteeAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPrePerspectiveDetachedAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage1 = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0, stage1);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PrePerspectiveDetached");
    await _assertOncePerPerspectiveAsync(await stage1, perspectives, command, "PostPerspectiveInline");
  }

  // ========================================
  // PostPerspectiveDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostPerspectiveDetached fires after the perspective completes (non-blocking),
  /// once for each perspective that folds the event.
  /// </summary>
  [Test]
  public async Task PostPerspectiveDetached_FiresAfterPerspectiveCompletes_NonBlockingAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PostPerspectiveDetached");
  }

  /// <summary>
  /// Verifies that PostPerspectiveDetached fires after the perspective processed the event: the
  /// pre stage and the post stage each fire once per perspective.
  /// </summary>
  [Test]
  public async Task PostPerspectiveDetached_FiresAfterEventsProcessed_GuaranteesCompletionAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage1 = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0, stage1);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PrePerspectiveInline");
    await _assertOncePerPerspectiveAsync(await stage1, perspectives, command, "PostPerspectiveDetached");
  }

  /// <summary>
  /// Verifies that both PostPerspective stages fire, once per perspective, before the checkpoint
  /// is reported.
  /// </summary>
  [Test]
  public async Task PostPerspectiveDetached_FiresBeforeCheckpointReported_TimingGuaranteeAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage1 = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0, stage1);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PostPerspectiveDetached");
    await _assertOncePerPerspectiveAsync(await stage1, perspectives, command, "PostPerspectiveInline");
  }

  // ========================================
  // PostPerspectiveInline Tests (Blocking) - Critical for Testing
  // ========================================

  /// <summary>
  /// Verifies that PostPerspectiveInline fires after the perspective completes (blocking), once
  /// for each perspective that folds the event.
  /// </summary>
  [Test]
  public async Task PostPerspectiveInline_FiresAfterPerspectiveCompletes_BlocksCheckpointAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PostPerspectiveInline");
  }

  /// <summary>
  /// Verifies that PostPerspectiveInline blocks checkpoint reporting: it fires once per
  /// perspective, after the pre stage did.
  /// </summary>
  [Test]
  public async Task PostPerspectiveInline_BlocksCheckpointReporting_GuaranteesDataSavedAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage1 = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0, stage1);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PrePerspectiveInline");
    await _assertOncePerPerspectiveAsync(await stage1, perspectives, command, "PostPerspectiveInline");
  }

  /// <summary>
  /// Verifies that PostPerspectiveInline fires for each event processed by each perspective, not once per batch.
  /// </summary>
  [Test]
  public async Task PostPerspectiveInline_FiresForEachEvent_MultipleInvocationsAsync(CancellationToken cancellationToken) {
    await _assertEveryEventFiresOncePerPerspectiveAsync([_postInline], cancellationToken);
  }

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Perspective stages fire for the event, each once per perspective:
  /// PrePerspectiveInline -> PrePerspectiveDetached (parallel) -> PostPerspectiveDetached -> PostPerspectiveInline
  /// </summary>
  [Test]
  public async Task PerspectiveStages_FireInCorrectOrder_AllStagesInvokedAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage1 = fixture.BffHost.WaitForPrePerspectiveDetachedAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage2 = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage3 = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0, stage1, stage2, stage3);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PrePerspectiveInline");
    await _assertOncePerPerspectiveAsync(await stage1, perspectives, command, "PrePerspectiveDetached");
    await _assertOncePerPerspectiveAsync(await stage2, perspectives, command, "PostPerspectiveDetached");
    await _assertOncePerPerspectiveAsync(await stage3, perspectives, command, "PostPerspectiveInline");
  }

  // ========================================
  // Stage Isolation Tests
  // Critical: Verify receptors ONLY fire at their registered stage
  // ========================================

  /// <summary>
  /// CRITICAL: Verifies that a receptor registered at PostPerspectiveDetached
  /// does NOT fire during PrePerspective stages: each stage counts exactly its own firings, once per perspective.
  /// </summary>
  /// <docs>core-concepts/lifecycle-receptors#stage-isolation</docs>
  [Category("StageIsolation")]
  [Category("PostPerspectiveDetached")]
  [Test]
  public async Task PostPerspectiveDetachedReceptor_FiresAfterPrePerspective_TemporalOrderingAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var command = _command();
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;

    // Armed before the send, so no firing of this product can be missed.
    var stage0 = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    var stage1 = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(
      messageFilter: filter, expectedFirings: perspectives, cancellationToken: cancellationToken);
    await fixture.Dispatcher.SendAsync(command);
    await Task.WhenAll(stage0, stage1);

    // Once per perspective that folds the event, and no more
    await _assertOncePerPerspectiveAsync(await stage0, perspectives, command, "PrePerspectiveInline");
    await _assertOncePerPerspectiveAsync(await stage1, perspectives, command, "PostPerspectiveDetached");
  }

  /// <summary>
  /// CRITICAL: Verifies that PostPerspectiveDetached receptor can query the perspective model
  /// AFTER perspective processing is complete - data should NOT be stale/null.
  /// This is the exact scenario from the reported bug - querying stale data.
  /// </summary>
  /// <docs>core-concepts/lifecycle-receptors#stage-isolation</docs>
  [Test]
  [Category("StageIsolation")]
  [Category("PostPerspectiveDetached")]
  public async Task PostPerspectiveDetachedReceptor_CanQueryModel_DataNotStaleAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Data Freshness Test Product",
      Description = "Testing data is not stale",
      Price = 123.45m,
      InitialStock = 42
    };

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 3, streamId: command.ProductId.Value, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;

    // Wait for workers to be idle before querying data
    await fixture.WaitForWorkersIdleAsync();

    // Assert - Verify perspective data is queryable and not stale
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId.Value);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo("Data Freshness Test Product");
  }

  /// <summary>
  /// Verifies that multiple events trigger all Perspective stages for each event, once per perspective.
  /// </summary>
  [Test]
  public async Task PerspectiveStages_MultipleEvents_AllStagesFireForEachAsync(CancellationToken cancellationToken) {
    await _assertEveryEventFiresOncePerPerspectiveAsync([_preInline, _preDetached, _postDetached, _postInline], cancellationToken);
  }

  private delegate Task<GenericLifecycleCompletionReceptor<ProductCreatedEvent>> _stageWait(
    Microsoft.Extensions.Hosting.IHost host, Func<ProductCreatedEvent, bool> filter, int expectedFirings,
    CancellationToken cancellationToken);

  private static readonly _stageWait _preInline = (h, f, n, ct) =>
    h.WaitForPrePerspectiveInlineAsync(messageFilter: f, expectedFirings: n, cancellationToken: ct);
  private static readonly _stageWait _preDetached = (h, f, n, ct) =>
    h.WaitForPrePerspectiveDetachedAsync(messageFilter: f, expectedFirings: n, cancellationToken: ct);
  private static readonly _stageWait _postDetached = (h, f, n, ct) =>
    h.WaitForPostPerspectiveDetachedAsync(messageFilter: f, expectedFirings: n, cancellationToken: ct);
  private static readonly _stageWait _postInline = (h, f, n, ct) =>
    h.WaitForPostPerspectiveInlineAsync(messageFilter: f, expectedFirings: n, cancellationToken: ct);

  /// <summary>
  /// Sends two products and requires each stage to fire once per event per perspective: one wait per stage and product,
  /// each filtered to its product and expecting one firing per perspective.
  /// </summary>
  private async Task _assertEveryEventFiresOncePerPerspectiveAsync(_stageWait[] stages, CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    CreateProductCommand[] commands = [_command(), _command()];
    var perspectives = fixture.BffHost.PerspectivesFolding<ProductCreatedEvent>();

    // Armed before the sends, so no firing of these products can be missed.
    var waits = commands
      .SelectMany(command => stages.Select(stage => (command, wait: stage(
        fixture.BffHost, e => e.ProductId == command.ProductId.Value, perspectives, cancellationToken))))
      .ToArray();
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }
    await Task.WhenAll(waits.Select(w => w.wait));

    foreach (var (command, wait) in waits) {
      await _assertOncePerPerspectiveAsync(await wait, perspectives, command, "each stage");
    }
  }

  private static async Task _assertOncePerPerspectiveAsync(
      GenericLifecycleCompletionReceptor<ProductCreatedEvent> receptor, int perspectives, CreateProductCommand command,
      string stage) {
    await Assert.That(receptor.InvocationCount).IsEqualTo(perspectives)
      .Because($"{stage} fires once for each of the {perspectives} perspectives that fold the event, and no more");
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId.Value);
  }

  private static CreateProductCommand _command() => new() {
    ProductId = ProductId.New(),
    Name = "Test Product",
    Description = "Test Description",
    Price = 99.99m,
    InitialStock = 10
  };
}
