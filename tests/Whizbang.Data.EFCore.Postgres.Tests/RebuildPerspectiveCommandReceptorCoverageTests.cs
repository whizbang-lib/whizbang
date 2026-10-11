// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for the synchronous fallback branch in
/// <see cref="RebuildPerspectiveCommandReceptor"/>'s exclude-stream resolution: when
/// <see cref="IEventStoreQuery.Query"/> is backed by a provider that does NOT implement
/// <see cref="IAsyncEnumerable{T}"/> (a plain in-memory/LINQ-to-Objects <c>IQueryable</c>, unlike the
/// real EF Core-backed store this receptor normally runs against — the shape every existing
/// Postgres-integration test for this receptor exercises), the receptor must still enumerate every
/// stream synchronously via <c>allStreams.AddRange(query)</c> rather than silently skip the
/// enumeration. No database: every dependency is an interface satisfied by an in-memory fake.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RebuildPerspectiveCommandReceptor.cs</code-under-test>
[Category("Shard1")]
public class RebuildPerspectiveCommandReceptorCoverageTests {

  // If the sync fallback silently produced no streams (or threw) for a non-async IQueryable
  // provider, an ExcludeStreamIds rebuild against it would either rebuild EVERY stream (the
  // exclusion list computed from an empty "all streams" set subtracts nothing) or crash outright —
  // either way the operator's "rebuild everything except these known-bad streams" request would not
  // do what it asked for.
  [Test]
  public async Task HandleAsync_ExcludeStreamIds_SyncOnlyEventStoreQuery_UsesTheNonAsyncFallbackAsync() {
    var kept1 = Guid.NewGuid();
    var kept2 = Guid.NewGuid();
    var excluded = Guid.NewGuid();
    var eventStoreQuery = new SyncOnlyEventStoreQuery([kept1, kept2, excluded]);
    var rebuilder = new RecordingRebuilder();
    var runnerRegistry = new FakeRunnerRegistry("TestPerspective");

    var services = new ServiceCollection();
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    services.AddSingleton<IPerspectiveRebuilder>(rebuilder);
    services.AddSingleton<IPerspectiveRunnerRegistry>(runnerRegistry);
    var sp = services.BuildServiceProvider();

    var receptor = new RebuildPerspectiveCommandReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<RebuildPerspectiveCommandReceptor>.Instance);

    var command = new RebuildPerspectiveCommand(
      PerspectiveNames: null,
      Mode: RebuildMode.InPlace,
      IncludeStreamIds: null,
      ExcludeStreamIds: [excluded]);

    await receptor.HandleAsync(command, CancellationToken.None);

    await Assert.That(rebuilder.LastStreamIds).IsNotNull()
      .Because("the exclude-filtered stream list must actually reach the rebuilder — a null here "
             + "means the sync fallback never ran or never populated the list");
    await Assert.That(rebuilder.LastStreamIds!.Count).IsEqualTo(2)
      .Because("the non-async provider's three streams, enumerated via the sync fallback, minus the "
             + "one excluded stream, must leave exactly two");
    await Assert.That(rebuilder.LastStreamIds).Contains(kept1);
    await Assert.That(rebuilder.LastStreamIds).Contains(kept2);
    await Assert.That(rebuilder.LastStreamIds).DoesNotContain(excluded);
  }

  // The caller's RequestId is what makes a broadcast rebuild answerable: every service that ran
  // stamps the same id on its rebuild events, so the caller can find its own rebuild across them.
  // Replacing it with a fresh id would make "did anything run" unanswerable for that caller.
  [Test]
  public async Task HandleAsync_WithARequestId_StampsTheCallersIdAndRequesterOnTheOriginAsync() {
    var requestId = Guid.NewGuid();
    var rebuilder = new RecordingRebuilder();
    var receptor = _receptor(rebuilder);

    await receptor.HandleAsync(new RebuildPerspectiveCommand(
      Mode: RebuildMode.InPlace,
      IncludeStreamIds: [Guid.NewGuid()],
      RequestId: requestId,
      RequestedBy: "operator@example.com"), CancellationToken.None);

    await Assert.That(rebuilder.LastOrigin).IsNotNull();
    await Assert.That(rebuilder.LastOrigin!.RequestId).IsEqualTo(requestId);
    await Assert.That(rebuilder.LastOrigin.RequestedBy).IsEqualTo("operator@example.com");
    await Assert.That(rebuilder.LastOrigin.Trigger).IsEqualTo(RebuildTrigger.Requested);
  }

  // Without a caller id each service mints its own, so the rebuild's events still carry an origin
  // id ("did anything run here" stays answerable per service) rather than none at all.
  [Test]
  public async Task HandleAsync_WithoutARequestId_StampsAFreshIdOnTheOriginAsync() {
    var rebuilder = new RecordingRebuilder();
    var receptor = _receptor(rebuilder);

    await receptor.HandleAsync(new RebuildPerspectiveCommand(
      Mode: RebuildMode.InPlace,
      IncludeStreamIds: [Guid.NewGuid()]), CancellationToken.None);

    await Assert.That(rebuilder.LastOrigin).IsNotNull();
    await Assert.That(rebuilder.LastOrigin!.RequestId).IsNotNull();
    await Assert.That(rebuilder.LastOrigin.RequestId).IsNotEqualTo(Guid.Empty);
    await Assert.That(rebuilder.LastOrigin.RequestedBy).IsNull();
    await Assert.That(rebuilder.LastOrigin.Trigger).IsEqualTo(RebuildTrigger.Requested);
  }

  private static RebuildPerspectiveCommandReceptor _receptor(RecordingRebuilder rebuilder) {
    var services = new ServiceCollection();
    services.AddSingleton<IEventStoreQuery>(new SyncOnlyEventStoreQuery([]));
    services.AddSingleton<IPerspectiveRebuilder>(rebuilder);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new FakeRunnerRegistry("TestPerspective"));
    var sp = services.BuildServiceProvider();
    return new RebuildPerspectiveCommandReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<RebuildPerspectiveCommandReceptor>.Instance);
  }

  // ── fakes ─────────────────────────────────────────────────────────────

  /// <summary>
  /// An <see cref="IEventStoreQuery"/> backed by a plain <c>List&lt;T&gt;.AsQueryable()</c> —
  /// LINQ-to-Objects' <c>EnumerableQuery&lt;T&gt;</c>, which does NOT implement
  /// <see cref="IAsyncEnumerable{T}"/>, unlike the real EF Core-backed implementation this receptor
  /// runs against in production. This is what forces the receptor's sync fallback path.
  /// </summary>
  private sealed class SyncOnlyEventStoreQuery(IReadOnlyList<Guid> streamIds) : IEventStoreQuery {
    private IReadOnlyList<EventStoreRecord> _records() => [.. streamIds.Select(id => new EventStoreRecord {
      StreamId = id,
      AggregateId = id,
      AggregateType = "Test",
      Version = 1,
      EventType = "Test",
      EventData = null,
      Metadata = null,
    })];

    public IQueryable<EventStoreRecord> Query => _records().AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query.Where(e => e.StreamId == streamId);
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query.Where(e => e.EventType == eventType);
  }

  private sealed class FakeRunnerRegistry(params string[] perspectiveNames) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      throw new NotSupportedException("Not exercised by this receptor.");
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [.. perspectiveNames.Select(n => new PerspectiveRegistrationInfo(n, $"global::Test.{n}", "global::Test.TestModel", []))];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class RecordingRebuilder : IPerspectiveRebuilder {
    public IReadOnlyList<Guid>? LastStreamIds { get; private set; }

    /// <summary>The origin the receptor passed down, so a dropped origin fails rather than passing quietly.</summary>
    public RebuildOrigin? LastOrigin { get; private set; }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) {
      LastStreamIds = [.. streamIds];
      return Task.FromResult(new RebuildResult(perspectiveName, LastStreamIds.Count, 0, TimeSpan.Zero, Success: true, Error: null));
    }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds,
        RebuildOrigin origin, CancellationToken ct = default) {
      LastOrigin = origin;
      return RebuildStreamsAsync(perspectiveName, streamIds, ct);
    }

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised — this test drives the ExcludeStreamIds path.");
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised — this test drives the ExcludeStreamIds path.");
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised — a stream filter is always present in this test.");
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised — a stream filter is always present in this test.");
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised by this receptor.");
  }
}
