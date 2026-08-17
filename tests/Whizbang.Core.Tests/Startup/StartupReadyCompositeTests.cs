using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Increment 4: <c>Ready</c> is a composite, not a boolean somebody sets. The pipeline state
/// derives "the blocking steps have drained" from the run plan the runner announces; the
/// <see cref="StartupReadyService"/> composes that with every registered readiness contributor
/// (transport subscription readiness among them) on the <c>IHostedLifecycleService.StartedAsync</c>
/// seam — the one hook that runs after every <c>StartAsync</c> has returned, which the framework
/// had never claimed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupReadiness.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineState.cs</code-under-test>
[Category("Startup")]
[NotInParallel(Order = 104)]
public class StartupReadyCompositeTests {

  private static StartupStepDescriptor _step(string name, bool blocking = true) =>
    new() { Name = name, Blocking = blocking };

  private static StartupStepResult _completed(string name) =>
    new(name, StartupStepOutcome.Completed, TimeSpan.Zero, null);

  private static StartupStepResult _failed(string name) =>
    new(name, StartupStepOutcome.Failed, TimeSpan.Zero, "boom");

  private static async Task _driveAsync(StartupPipelineState state, StartupRunPlan plan, params StartupStepResult[] results) {
    await state.OnRunStartingAsync(plan, CancellationToken.None);
    foreach (var result in results) {
      await state.OnStepStartingAsync(new StartupStepContext(_step(result.Name)), CancellationToken.None);
      await state.OnStepCompletedAsync(result, CancellationToken.None);
    }
  }

  // ── the state's readiness: blocking band only ──────────────────────────

  [Test]
  public async Task State_IsReady_WhenBlockingStepsDrain_WhileNonBlockingStillRunAsync() {
    var state = new StartupPipelineState();
    var plan = new StartupRunPlan([_step("Migrate"), _step("Reconcile"), _step("Rewrite", blocking: false)]);

    await _driveAsync(state, plan, _completed("Migrate"), _completed("Reconcile"));

    await Assert.That(state.IsReady).IsTrue()
      .Because("non-blocking steps live in the post-ready band — they must never gate Ready");
    await Assert.That(state.IsComplete).IsFalse()
      .Because("the run itself is still going: Rewrite has not finished, only readiness has fired");
    await state.WaitForReadyAsync(CancellationToken.None);
  }

  [Test]
  public async Task State_IsNotReady_WhileABlockingStepIsStillPendingAsync() {
    var state = new StartupPipelineState();
    var plan = new StartupRunPlan([_step("Migrate"), _step("Reconcile")]);

    await _driveAsync(state, plan, _completed("Migrate"));

    await Assert.That(state.IsReady).IsFalse()
      .Because("Reconcile is blocking and has not drained — ready now would be ready too early");
  }

  [Test]
  public async Task State_NeverReady_WhenABlockingStepFailsAsync() {
    var state = new StartupPipelineState();
    var plan = new StartupRunPlan([_step("Migrate"), _step("Reconcile")]);

    await _driveAsync(state, plan, _failed("Migrate"), _completed("Reconcile"));
    await state.OnPipelineCompletedAsync(new StartupSummary([]), CancellationToken.None);

    await Assert.That(state.IsReady).IsFalse()
      .Because("fail-closed: a failed blocking step means this run never reports ready, exactly "
             + "as the schema gate never opens on a failed migration");
    await Assert.That(state.IsComplete).IsTrue()
      .Because("the run finished — completion and readiness are different facts");
  }

  [Test]
  public async Task State_SkippedBlockingStep_CountsAsDrainedAsync() {
    var state = new StartupPipelineState();
    var plan = new StartupRunPlan([_step("Migrate"), _step("Repair")]);

    await _driveAsync(state, plan,
      _completed("Migrate"),
      new StartupStepResult("Repair", StartupStepOutcome.Skipped, TimeSpan.Zero, "nothing to repair"));

    await Assert.That(state.IsReady).IsTrue()
      .Because("a step that deliberately did nothing has drained — Skipped is an outcome, not a failure");
  }

  [Test]
  public async Task State_ReEntry_ResetsReadinessAsync() {
    var state = new StartupPipelineState();
    var plan = new StartupRunPlan([_step("Migrate")]);

    await _driveAsync(state, plan, _completed("Migrate"));
    await Assert.That(state.IsReady).IsTrue();

    // A reviving instance re-enters the pipeline; reporting the OLD run as ready would tell it
    // it is up when it is not.
    await state.OnRunStartingAsync(plan, CancellationToken.None);
    await Assert.That(state.IsReady).IsFalse()
      .Because("re-entry must reset readiness — the previous run's answer is no longer true");

    await state.OnStepStartingAsync(new StartupStepContext(_step("Migrate")), CancellationToken.None);
    await state.OnStepCompletedAsync(_completed("Migrate"), CancellationToken.None);
    await Assert.That(state.IsReady).IsTrue();
  }

  [Test]
  public async Task State_PlanWithNoBlockingSteps_IsReadyAtRunStartAsync() {
    var state = new StartupPipelineState();

    await state.OnRunStartingAsync(new StartupRunPlan([_step("Rewrite", blocking: false)]), CancellationToken.None);

    await Assert.That(state.IsReady).IsTrue()
      .Because("with nothing blocking there is nothing to drain");
  }

  // ── the runner announces the plan ───────────────────────────────────────

  private sealed class _recordingObserver : IStartupStepObserver {
    public List<string> Events { get; } = [];
    public StartupRunPlan? Plan { get; private set; }
    public ValueTask OnRunStartingAsync(StartupRunPlan plan, CancellationToken cancellationToken) {
      Events.Add("run-starting");
      Plan = plan;
      return ValueTask.CompletedTask;
    }
    public ValueTask OnStepStartingAsync(StartupStepContext context, CancellationToken cancellationToken) {
      Events.Add($"starting:{context.Descriptor.Name}");
      return ValueTask.CompletedTask;
    }
    public ValueTask OnStepCompletedAsync(StartupStepResult result, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask OnPipelineCompletedAsync(StartupSummary summary, CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  private sealed class _inertStep : IStartupStep {
    public _inertStep(string name, bool blocking = true) {
      Descriptor = new StartupStepDescriptor { Name = name, Blocking = blocking };
    }
    public StartupStepDescriptor Descriptor { get; }
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) =>
      new(new StartupStepReport(StartupStepOutcome.Completed));
  }

  [Test]
  public async Task Runner_AnnouncesThePlan_BeforeTheFirstStepAsync() {
    var observer = new _recordingObserver();
    var runner = new StartupPipelineRunner([new _inertStep("A"), new _inertStep("B", blocking: false)], [observer]);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(observer.Events[0]).IsEqualTo("run-starting")
      .Because("readiness is only computable by an observer that knows which steps are coming");
    await Assert.That(observer.Plan!.Steps.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Runner_DrivenState_ReportsReadyThroughTheRealNotificationsAsync() {
    var state = new StartupPipelineState();
    var runner = new StartupPipelineRunner([new _inertStep("A"), new _inertStep("B", blocking: false)], [state]);

    await runner.RunAsync(CancellationToken.None);

    await Assert.That(state.IsReady).IsTrue()
      .Because("the state derives readiness from the same notifications every observer gets");
  }

  // ── the composite service on the StartedAsync seam ─────────────────────

  private sealed class _tcsContributor(string name) : IStartupReadinessContributor {
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string ContributorName => name;
    public void MarkReady() => _tcs.TrySetResult();
    public Task WaitForContributorReadyAsync(CancellationToken cancellationToken) => _tcs.Task.WaitAsync(cancellationToken);
  }

  [Test]
  public async Task ReadyService_SignalsOnlyAfterTheStateAndEveryContributorAsync() {
    var state = new StartupPipelineState();
    var signal = new StartupReadySignal();
    var subscriptions = new _tcsContributor("subscriptions");
    var service = new StartupReadyService(state, signal, [subscriptions]);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var started = service.StartedAsync(cts.Token);

    await Task.Delay(100);
    await Assert.That(signal.IsReady).IsFalse()
      .Because("neither the blocking steps nor the contributor have answered yet");

    await _driveAsync(state, new StartupRunPlan([_step("Migrate")]), _completed("Migrate"));
    await Task.Delay(100);
    await Assert.That(signal.IsReady).IsFalse()
      .Because("the pipeline drained but the transport has not subscribed — Ready is a composite, "
             + "not the pipeline alone");

    subscriptions.MarkReady();
    await started;
    await Assert.That(signal.IsReady).IsTrue();
    await signal.WaitForReadyAsync(CancellationToken.None);
  }

  [Test]
  public async Task ReadyService_WithNoContributors_SignalsOnBlockingDrainAloneAsync() {
    var state = new StartupPipelineState();
    var signal = new StartupReadySignal();
    var service = new StartupReadyService(state, signal);

    await _driveAsync(state, new StartupRunPlan([_step("Migrate")]), _completed("Migrate"));
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await service.StartedAsync(cts.Token);

    await Assert.That(signal.IsReady).IsTrue();
  }

  // ── the workers really are contributors over SubscriptionsReady ────────

  [Test]
  public async Task TransportConsumerWorker_ContributesItsSubscriptionsReadySignalAsync() {
    var gate = new SchemaReadyGate();
    using var sp = new ServiceCollection().BuildServiceProvider();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new Whizbang.Core.Transports.TransportDestination("dest-a"));
    var worker = new TransportConsumerWorker(
      new Whizbang.Core.Transports.InProcessTransport(),
      options,
      new Whizbang.Core.Resilience.SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      JsonContextRegistry.CreateCombinedOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      schemaReadyGate: gate);
    IStartupReadinessContributor contributor = worker;

    await Assert.That(contributor.ContributorName).IsEqualTo("transport-consumer");

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var waiting = contributor.WaitForContributorReadyAsync(cts.Token);
    await Task.Delay(200);
    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("the worker is held at the schema gate, so its contribution must be too");

    gate.MarkReady();
    await waiting.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ServiceBusConsumerWorker_ContributesItsSubscriptionsReadySignalAsync() {
    using var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new ServiceBusConsumerWorker(
      new Whizbang.Core.Transports.InProcessTransport(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<ServiceBusConsumerWorker>.Instance,
      new OrderedStreamProcessor(),
      new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("t", "s")] });
    IStartupReadinessContributor contributor = worker;

    await Assert.That(contributor.ContributorName).IsEqualTo("servicebus-consumer");

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await contributor.WaitForContributorReadyAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
