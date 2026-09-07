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
    var eventStoreQuery = new _syncOnlyEventStoreQuery([kept1, kept2, excluded]);
    var rebuilder = new _recordingRebuilder();
    var runnerRegistry = new _fakeRunnerRegistry("TestPerspective");

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

  // ── fakes ─────────────────────────────────────────────────────────────

  /// <summary>
  /// An <see cref="IEventStoreQuery"/> backed by a plain <c>List&lt;T&gt;.AsQueryable()</c> —
  /// LINQ-to-Objects' <c>EnumerableQuery&lt;T&gt;</c>, which does NOT implement
  /// <see cref="IAsyncEnumerable{T}"/>, unlike the real EF Core-backed implementation this receptor
  /// runs against in production. This is what forces the receptor's sync fallback path.
  /// </summary>
  private sealed class _syncOnlyEventStoreQuery(IReadOnlyList<Guid> streamIds) : IEventStoreQuery {
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

  private sealed class _fakeRunnerRegistry(params string[] perspectiveNames) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      throw new NotSupportedException("Not exercised by this receptor.");
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [.. perspectiveNames.Select(n => new PerspectiveRegistrationInfo(n, $"global::Test.{n}", "global::Test.TestModel", []))];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class _recordingRebuilder : IPerspectiveRebuilder {
    public IReadOnlyList<Guid>? LastStreamIds { get; private set; }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) {
      LastStreamIds = [.. streamIds];
      return Task.FromResult(new RebuildResult(perspectiveName, LastStreamIds.Count, 0, TimeSpan.Zero, Success: true, Error: null));
    }

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised — this test drives the ExcludeStreamIds path.");
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised — a stream filter is always present in this test.");
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised by this receptor.");
  }
}
