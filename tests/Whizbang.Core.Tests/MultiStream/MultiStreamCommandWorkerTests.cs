// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Events.System;
using Whizbang.Core.Lenses;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.MultiStream;

/// <summary>
/// One pass of the multi-stream command worker (#1358): claim this instance's streams, call each one's handler under the
/// run's scope, record every outcome, and announce the runs that completed. Driven through the single-pass seam, one flow
/// on a fake store and a fake clock; the hosted loop is covered in the component tests.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#worker</docs>
[Category("MultiStream")]
[NotInParallel(["AmbientScope", "MessageParseDiagnostics"])]
public class MultiStreamCommandWorkerTests {
  private static readonly Guid _instance = Guid.Parse("0199d000-0000-7000-8000-000000000001");
  private static readonly Guid _run = Guid.Parse("0199d000-0000-7000-8000-0000000000aa");
  private static readonly Guid _streamA = Guid.Parse("0199d000-0000-7000-8000-0000000000a1");
  private static readonly Guid _streamB = Guid.Parse("0199d000-0000-7000-8000-0000000000a2");
  private static readonly string _probeType = TypeNameFormatter.Format(typeof(ProbeMultiStreamCommand));

  private sealed class Harness {
    public ScriptedMultiStreamStore Store { get; } = new();
    public ProbeMultiStreamHandler Handler { get; } = new();
    public RebuildEventDispatcherDouble Dispatcher { get; init; } = new();
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-10-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    public MultiStreamCommandOptions Options { get; } = new();
    public IPartitionAssignmentSource? Assignment { get; init; }
    public Action<IServiceCollection>? Configure { get; init; }

    public MultiStreamCommandWorker Build() {
      var services = new ServiceCollection();
      services.AddMultiStreamCommandHandler<ProbeMultiStreamCommand, ProbeMultiStreamHandler>();
      services.AddSingleton(Handler);
      services.AddSingleton<IDispatcher>(Dispatcher);
      Configure?.Invoke(services);
      var provider = services.BuildServiceProvider();
      return new MultiStreamCommandWorker(
        Store,
        provider.GetServices<IMultiStreamCommandBinding>(),
        provider.GetRequiredService<IServiceScopeFactory>(),
        new FixedInstance(_instance),
        Microsoft.Extensions.Options.Options.Create(Options),
        NullLogger<MultiStreamCommandWorker>.Instance,
        Assignment ?? new FixedAssignmentSource(null),
        SchemaReadyGate.AlreadyReady(),
        Time);
    }
  }

  private static string _payload(string label = "w") =>
    MultiStreamJson.Serialize(new ProbeMultiStreamCommand(_run, [_streamA, _streamB], label));

  private static MultiStreamWorkItem _item(Guid stream, string? scope = null, string? type = null) =>
    new(_run, stream, type ?? _probeType, _payload(), scope, 1);

  [Test]
  public async Task RunOnceAsync_CallsTheHandlerOncePerClaimedStreamAndRecordsEachSuccessAsync() {
    var h = new Harness();
    h.Store.Claims.Enqueue([_item(_streamA), _item(_streamB)]);
    var worker = h.Build();

    var claimed = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(claimed).IsEqualTo(2);
    await Assert.That(h.Handler.Calls.Select(c => c.StreamId)).IsEquivalentTo([_streamA, _streamB], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(h.Handler.Calls[0].Command.Label).IsEqualTo("w");
    await Assert.That(h.Store.Completed.Single().Outcomes).IsEquivalentTo([
      new MultiStreamOutcome(_run, _streamA, null), new MultiStreamOutcome(_run, _streamB, null)]);
    await Assert.That(h.Store.Completed[0].InstanceId).IsEqualTo(_instance);
    await Assert.That(h.Store.Completed[0].MaxAttempts).IsEqualTo(h.Options.MaxAttempts);
    await Assert.That(h.Store.Completed[0].ReleaseDelay).IsEqualTo(h.Options.NoHandlerRetryDelay);
  }

  [Test]
  public async Task RunOnceAsync_ClaimsAsThisInstanceWithTheConfiguredBatchAndLeaseAsync() {
    var h = new Harness();
    h.Options.BatchSize = 7;
    h.Options.Lease = TimeSpan.FromSeconds(90);
    var worker = h.Build();

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(h.Store.ClaimRequests.Single()).IsEqualTo(new MultiStreamClaim(_instance, 7, TimeSpan.FromSeconds(90), null));
  }

  [Test]
  public async Task RunOnceAsync_ClaimsUnderTheCachedPartitionAssignmentAsync() {
    var version = new PartitionAssignmentVersion(4, 9);
    var h = new Harness { Assignment = new FixedAssignmentSource(version) };
    var worker = h.Build();

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(h.Store.ClaimRequests.Single().Assignment).IsEqualTo(version)
      .Because("the claim ranks this instance exactly as the work claim does, from the same cached assignment");
  }

  [Test]
  public async Task RunOnceAsync_NothingToClaim_RecordsNothingAsync() {
    var h = new Harness();
    var worker = h.Build();

    var claimed = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(claimed).IsEqualTo(0);
    await Assert.That(h.Store.Completed).IsEmpty();
  }

  [Test]
  public async Task RunOnceAsync_AHandlerThatThrows_RecordsTheFailureAndWorksTheRestAsync() {
    var h = new Harness { Configure = s => s.AddSingleton<ProbeMultiStreamHandler>(new ThrowingOnStreamHandler(_streamA)) };
    h.Store.Claims.Enqueue([_item(_streamA), _item(_streamB)]);
    var worker = h.Build();

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(h.Store.Completed.Single().Outcomes).IsEquivalentTo([
      new MultiStreamOutcome(_run, _streamA, "InvalidOperationException: refused " + _streamA),
      new MultiStreamOutcome(_run, _streamB, null)]);
  }

  [Test]
  public async Task RunOnceAsync_AHandlerTimingOutOnItsOwn_IsAFailureNotAShutdownAsync() {
    var h = new Harness { Configure = s => s.AddSingleton<ProbeMultiStreamHandler>(new TimingOutHandler()) };
    h.Store.Claims.Enqueue([_item(_streamA), _item(_streamB)]);
    var worker = h.Build();

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(h.Store.Completed.Single().Outcomes.Select(o => o.Error ?? "")).IsEquivalentTo([
      "TaskCanceledException: timed out", "TaskCanceledException: timed out"])
      .Because("only the host stopping ends a pass; a handler's own cancellation is a failed attempt, retried");
  }

  [Test]
  public async Task RunOnceAsync_ACommandThisInstanceHasNoHandlerFor_IsReleasedWithoutCountingAnAttemptAsync() {
    var h = new Harness();
    h.Options.NoHandlerRetryDelay = TimeSpan.FromSeconds(45);
    h.Store.Claims.Enqueue([_item(_streamA, type: "Unknown.Command, Elsewhere")]);
    var worker = h.Build();

    _ = await worker.RunOnceAsync(CancellationToken.None);

    var outcome = h.Store.Completed.Single().Outcomes.Single();
    await Assert.That(outcome.Released).IsTrue()
      .Because("an instance that owns a stream but not the handler (a rolling update) must not use up the stream's attempts");
    await Assert.That(outcome.Error).Contains("Unknown.Command, Elsewhere");
    await Assert.That(h.Store.Completed[0].ReleaseDelay).IsEqualTo(TimeSpan.FromSeconds(45));
    await Assert.That(h.Handler.Calls).IsEmpty();
  }

  [Test]
  public async Task RunOnceAsync_RunsTheHandlerUnderTheRunsScopeAndPutsTheAmbientScopeBackAsync() {
    var capture = new ScopeCapturingHandler();
    var h = new Harness { Configure = s => s.AddSingleton<ProbeMultiStreamHandler>(capture) };
    var scope = MultiStreamScope.Capture(new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-run" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    });
    h.Store.Claims.Enqueue([_item(_streamA, scope)]);
    var worker = h.Build();
    var ambient = ScopeContextAccessor.CurrentContext;

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(capture.Seen.Single()?.Scope.TenantId).IsEqualTo("tenant-run");
    await Assert.That(ScopeContextAccessor.CurrentContext).IsSameReferenceAs(ambient)
      .Because("the run's scope applies to its handler call only");
  }

  [Test]
  public async Task RunOnceAsync_ARunWithNoScope_RunsTheHandlerWithNoScopeAsync() {
    var capture = new ScopeCapturingHandler();
    var h = new Harness { Configure = s => s.AddSingleton<ProbeMultiStreamHandler>(capture) };
    h.Store.Claims.Enqueue([_item(_streamA)]);
    var worker = h.Build();

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(capture.Seen.Single()).IsNull()
      .Because("a command sent with no scope is worked with none, not under whatever the worker's thread carries");
  }

  [Test]
  public async Task RunOnceAsync_ARunThatCompleted_IsPublishedAndAnnouncedAsync() {
    var h = new Harness();
    h.Store.Claims.Enqueue([_item(_streamA)]);
    var completion = new MultiStreamRunCompletion(_run, _probeType, 1, 1, 0);
    h.Store.CompletionsToReturn.Enqueue([completion]);
    var worker = h.Build();
    MultiStreamRunCompletion? announced = null;
    worker.OnRunCompleted += c => announced = c;

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(announced).IsEqualTo(completion);
    var published = (MultiStreamCommandCompleted)h.Dispatcher.Published.Single();
    await Assert.That(published.StreamId).IsEqualTo(_run)
      .Because("the completion event lives in the run's own stream, never in one of the streams the run worked");
    await Assert.That(published.CommandType).IsEqualTo(_probeType);
    await Assert.That(published.Succeeded).IsEqualTo(1);
    await Assert.That(published.Failed).IsEqualTo(0);
    await Assert.That(published.StreamCount).IsEqualTo(1);
  }

  [Test]
  public async Task RunOnceAsync_ARunThatCompleted_WithNobodyListening_IsStillPublishedAsync() {
    var h = new Harness();
    h.Store.Claims.Enqueue([_item(_streamA)]);
    h.Store.CompletionsToReturn.Enqueue([new MultiStreamRunCompletion(_run, _probeType, 1, 1, 0)]);
    var worker = h.Build();

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(h.Dispatcher.Published.OfType<MultiStreamCommandCompleted>().Single().StreamId).IsEqualTo(_run)
      .Because("the completion event is the durable record; the in-process announcement is optional");
  }

  [Test]
  public async Task RunOnceAsync_ACompletionThatCannotBePublished_IsStillAnnouncedAsync() {
    var h = new Harness { Dispatcher = new RebuildEventDispatcherDouble(throwOnPublish: true) };
    h.Store.Claims.Enqueue([_item(_streamA)]);
    h.Store.CompletionsToReturn.Enqueue([new MultiStreamRunCompletion(_run, _probeType, 1, 1, 0)]);
    var worker = h.Build();
    var announced = 0;
    worker.OnRunCompleted += _ => announced++;

    await Assert.That(async () => await worker.RunOnceAsync(CancellationToken.None)).ThrowsNothing()
      .Because("the streams were worked and recorded; a failed announcement must not undo or repeat that");
    await Assert.That(announced).IsEqualTo(1);
  }

  [Test]
  public async Task RunOnceAsync_WithNoDispatcher_StillAnnouncesTheCompletionAsync() {
    var h = new Harness { Configure = s => s.RemoveAll<IDispatcher>() };
    h.Store.Claims.Enqueue([_item(_streamA)]);
    h.Store.CompletionsToReturn.Enqueue([new MultiStreamRunCompletion(_run, _probeType, 1, 1, 0)]);
    var worker = h.Build();
    var announced = 0;
    worker.OnRunCompleted += _ => announced++;

    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(announced).IsEqualTo(1);
  }

  [Test]
  public async Task RunOnceAsync_CanceledMidBatch_RecordsTheStreamsAlreadyDoneAndStopsAsync() {
    using var cts = new CancellationTokenSource();
    var h = new Harness { Configure = s => s.AddSingleton<ProbeMultiStreamHandler>(new CancelingAfterStreamHandler(_streamA, cts)) };
    h.Store.Claims.Enqueue([_item(_streamA), _item(_streamB)]);
    var worker = h.Build();

    await Assert.That(async () => await worker.RunOnceAsync(cts.Token)).Throws<OperationCanceledException>();

    await Assert.That(h.Store.Completed.Single().Outcomes).IsEquivalentTo([new MultiStreamOutcome(_run, _streamA, null)])
      .Because("the finished stream is recorded; the unfinished one records nothing and is offered again when its lease lapses");
  }

  [Test]
  public async Task RunOnceAsync_CanceledBeforeAnyStreamFinished_RecordsNothingAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var h = new Harness();
    h.Store.Claims.Enqueue([_item(_streamA)]);
    var worker = h.Build();

    await Assert.That(async () => await worker.RunOnceAsync(cts.Token)).Throws<OperationCanceledException>();

    await Assert.That(h.Store.Completed).IsEmpty();
  }

  [Test]
  public async Task PurgeIfDueAsync_PurgesRunsOlderThanTheRetentionOncePerIntervalAsync() {
    var h = new Harness();
    h.Options.CompletedRunRetention = TimeSpan.FromDays(2);
    h.Options.PurgeInterval = TimeSpan.FromHours(1);
    var worker = h.Build();

    await worker.PurgeIfDueAsync(CancellationToken.None);
    await worker.PurgeIfDueAsync(CancellationToken.None);
    h.Time.Advance(TimeSpan.FromHours(1));
    await worker.PurgeIfDueAsync(CancellationToken.None);

    await Assert.That(h.Store.Purges).IsEquivalentTo([
      DateTimeOffset.Parse("2026-10-11T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
      DateTimeOffset.Parse("2026-10-11T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture)],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task RequestImmediateRun_WhenAWakeIsAlreadyPending_IsHarmlessAsync() {
    var worker = new Harness().Build();

    worker.OnNotification("run-1");

    await Assert.That(() => worker.RequestImmediateRun()).ThrowsNothing()
      .Because("one pending wake already covers every run announced before the pass starts");
    await Assert.That(worker.ChannelName).IsEqualTo(MultiStreamCommandWorker.CHANNEL);
  }

  [Test]
  public async Task Constructor_NullDependencies_ThrowAsync() {
    var h = new Harness();
    var services = new ServiceCollection().BuildServiceProvider();
    var scopes = services.GetRequiredService<IServiceScopeFactory>();
    var options = Microsoft.Extensions.Options.Options.Create(new MultiStreamCommandOptions());
    var logger = NullLogger<MultiStreamCommandWorker>.Instance;
    var instance = new FixedInstance(_instance);
    var source = new FixedAssignmentSource(null);
    var gate = SchemaReadyGate.AlreadyReady();

    await Assert.That(() => new MultiStreamCommandWorker(null!, [], scopes, instance, options, logger, source, gate)).Throws<ArgumentNullException>();
    await Assert.That(() => new MultiStreamCommandWorker(h.Store, null!, scopes, instance, options, logger, source, gate)).Throws<ArgumentNullException>();
    await Assert.That(() => new MultiStreamCommandWorker(h.Store, [], null!, instance, options, logger, source, gate)).Throws<ArgumentNullException>();
    await Assert.That(() => new MultiStreamCommandWorker(h.Store, [], scopes, null!, options, logger, source, gate)).Throws<ArgumentNullException>();
    await Assert.That(() => new MultiStreamCommandWorker(h.Store, [], scopes, instance, null!, logger, source, gate)).Throws<ArgumentNullException>();
    await Assert.That(() => new MultiStreamCommandWorker(h.Store, [], scopes, instance, options, null!, source, gate)).Throws<ArgumentNullException>();
    await Assert.That(() => new MultiStreamCommandWorker(h.Store, [], scopes, instance, options, logger, null!, gate)).Throws<ArgumentNullException>();
    await Assert.That(() => new MultiStreamCommandWorker(h.Store, [], scopes, instance, options, logger, source, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  [Arguments(0, 60, 3, 1, 60, 60, 60)]
  [Arguments(10, 0, 3, 1, 60, 60, 60)]
  [Arguments(10, 60, 0, 1, 60, 60, 60)]
  [Arguments(10, 60, 3, 0, 60, 60, 60)]
  [Arguments(10, 60, 3, 1, 0, 60, 60)]
  [Arguments(10, 60, 3, 1, 60, 0, 60)]
  [Arguments(10, 60, 3, 1, 60, 60, 0)]
  public async Task Options_Validate_RefusesANonPositiveSettingAsync(
      int batch, int leaseSeconds, int attempts, int pollSeconds, int retentionSeconds, int purgeSeconds, int noHandlerSeconds) {
    var options = new MultiStreamCommandOptions {
      NoHandlerRetryDelay = TimeSpan.FromSeconds(noHandlerSeconds),
      BatchSize = batch,
      Lease = TimeSpan.FromSeconds(leaseSeconds),
      MaxAttempts = attempts,
      PollInterval = TimeSpan.FromSeconds(pollSeconds),
      CompletedRunRetention = TimeSpan.FromSeconds(retentionSeconds),
      PurgeInterval = TimeSpan.FromSeconds(purgeSeconds)
    };

    await Assert.That(options.Validate).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Options_Validate_AcceptsTheDefaultsAsync() {
    await Assert.That(new MultiStreamCommandOptions().Validate).ThrowsNothing();
  }

  // ── Parse counts (#1526): how many times a run's stored payload and scope are read back per pass ──

  private static string _scopeJson() => MultiStreamScope.Capture(new ScopeContext {
    Scope = new PerspectiveScope { TenantId = "tenant-p", UserId = "user-p" },
    Roles = new HashSet<string>(),
    Permissions = new HashSet<Permission>(),
    SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
    Claims = new Dictionary<string, string>()
  })!;

  private static List<MultiStreamWorkItem> _streamsOf(Guid runId, int streams, string payload, string? scope) =>
    [.. Enumerable.Range(0, streams).Select(_ => new MultiStreamWorkItem(runId, TrackedGuid.New().Value, _probeType, payload, scope, 1))];

  [Test]
  public async Task ParseCount_OneRunOfEightStreams_ReadsThePayloadOnceAndTheScopeOncePerStreamAsync() {
    var runId = TrackedGuid.New().Value;
    using var parses = new MessageParseRecorder(runId);
    // The store hands every stream of a run the same payload string, as the Postgres claim does.
    var payload = MultiStreamJson.Serialize(new ProbeMultiStreamCommand(runId, [], "w"));
    var h = new Harness();
    h.Store.Claims.Enqueue(_streamsOf(runId, 8, payload, _scopeJson()));

    _ = await h.Build().RunOnceAsync(CancellationToken.None);

    await Assert.That(h.Handler.Calls.Count).IsEqualTo(8);
    await Assert.That(parses.CountFor(runId, MessageParseKind.Payload)).IsEqualTo(1)
      .Because("the binding's one-slot read-back hands one parsed command to every stream of the run");
    await Assert.That(parses.CountFor(runId, MessageParseKind.Scope)).IsEqualTo(8)
      .Because("the run's scope is read back for every stream; nothing caches it");
  }

  [Test]
  public async Task ParseCount_TwoRunsInterleavedInOneBatch_ReadsEachPayloadOncePerStreamAsync() {
    var first = TrackedGuid.New().Value;
    var second = TrackedGuid.New().Value;
    using var parses = new MessageParseRecorder(first, second);
    var firstPayload = MultiStreamJson.Serialize(new ProbeMultiStreamCommand(first, [], "a"));
    var secondPayload = MultiStreamJson.Serialize(new ProbeMultiStreamCommand(second, [], "b"));
    var a = _streamsOf(first, 4, firstPayload, null);
    var b = _streamsOf(second, 4, secondPayload, null);
    var h = new Harness();
    h.Store.Claims.Enqueue([.. a.Zip(b).SelectMany(pair => new[] { pair.First, pair.Second })]);

    _ = await h.Build().RunOnceAsync(CancellationToken.None);

    await Assert.That(parses.CountFor(first, MessageParseKind.Payload)).IsEqualTo(4)
      .Because("one slot holds one run: each switch between runs evicts the other, so an interleaved batch reads every stream's payload");
    await Assert.That(parses.CountFor(second, MessageParseKind.Payload)).IsEqualTo(4);
    await Assert.That(parses.CountFor(first, MessageParseKind.Scope)).IsEqualTo(0)
      .Because("a run sent with no scope has none to read back");
  }

  [Test]
  public async Task ParseCount_TheSameRunInALaterBatch_IsNotReadAgainAsync() {
    var runId = TrackedGuid.New().Value;
    using var parses = new MessageParseRecorder(runId);
    var command = new ProbeMultiStreamCommand(runId, [], "w");
    var h = new Harness();
    // A later claim reads the payload into a new string with the same text.
    h.Store.Claims.Enqueue(_streamsOf(runId, 2, MultiStreamJson.Serialize(command), null));
    h.Store.Claims.Enqueue(_streamsOf(runId, 2, MultiStreamJson.Serialize(command), null));
    var worker = h.Build();

    _ = await worker.RunOnceAsync(CancellationToken.None);
    _ = await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(parses.CountFor(runId, MessageParseKind.Payload)).IsEqualTo(1)
      .Because("a new string with the same text is matched by content, at the cost of comparing the whole payload once per stream");
  }

  private sealed class FixedInstance(Guid id) : IServiceInstanceProvider {
    public Guid InstanceId => id;
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = id, ServiceName = "test", HostName = "test-host", ProcessId = 1 };
  }

  private sealed class FixedAssignmentSource(PartitionAssignmentVersion? version) : IPartitionAssignmentSource {
    public PartitionAssignment? Current => null;
    public event Action<PartitionAssignment?>? OnAssignmentChanged;
    public void RaiseUnused() => OnAssignmentChanged?.Invoke(null);
    public void MarkStale() => RaiseUnused();
    public ValueTask<PartitionAssignmentVersion?> ForClaimAsync(Guid instanceId, CancellationToken cancellationToken) =>
      ValueTask.FromResult<PartitionAssignmentVersion?>(version);
  }
}
