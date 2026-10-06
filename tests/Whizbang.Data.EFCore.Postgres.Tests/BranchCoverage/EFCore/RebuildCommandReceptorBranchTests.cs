// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="RebuildPerspectiveCommandReceptor"/>: an exclude-filtered rebuild
/// over the real EF Core event-store query enumerates its streams asynchronously, and a failed
/// rebuild is logged with its error or, when it carries none, with an explicit placeholder.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RebuildPerspectiveCommandReceptor.cs</code-under-test>
[Category("Shard1")]
public class RebuildCommandReceptorBranchTests : EFCoreTestBase {

  [Test]
  public async Task ExcludeStreamIds_OverTheEfQuery_EnumeratesAsynchronouslyAndDropsTheExcludedAsync() {
    var kept = Guid.CreateVersion7();
    var excluded = Guid.CreateVersion7();
    await using (var seed = CreateDbContext()) {
      var store = new EFCoreEventStore<WorkCoordinationDbContext>(seed);
      await store.AppendAsync(kept, _envelope());
      await store.AppendAsync(excluded, _envelope());
    }
    await using var queryContext = CreateDbContext();
    var rebuilder = new RecordingRebuilder(new RebuildResult("P", 1, 0, TimeSpan.Zero, Success: true, Error: null));
    var services = new ServiceCollection();
    services.AddSingleton<IEventStoreQuery>(new EFCoreFilterableEventStoreQuery(queryContext));
    services.AddSingleton<IPerspectiveRebuilder>(rebuilder);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new OneRegistry("P"));
    await using var sp = services.BuildServiceProvider();
    var receptor = new RebuildPerspectiveCommandReceptor(sp.GetRequiredService<IServiceScopeFactory>(), new CapturingLogger());

    await receptor.HandleAsync(new RebuildPerspectiveCommand(
      PerspectiveNames: null, Mode: RebuildMode.InPlace, IncludeStreamIds: null, ExcludeStreamIds: [excluded]), CancellationToken.None);

    await Assert.That(rebuilder.LastStreamIds).IsNotNull();
    await Assert.That(rebuilder.LastStreamIds).Contains(kept);
    await Assert.That(rebuilder.LastStreamIds).DoesNotContain(excluded);
  }

  [Test]
  public async Task FailedRebuild_IsLoggedWithItsError_OrAPlaceholderWhenItHasNoneAsync() {
    var withError = await _failAsync("index build failed");
    var withoutError = await _failAsync(null);

    await Assert.That(withError.Messages.Any(m => m.Contains("failed: index build failed", StringComparison.Ordinal))).IsTrue();
    await Assert.That(withoutError.Messages.Any(m => m.Contains("failed: (no error message)", StringComparison.Ordinal))).IsTrue()
      .Because("a failure that carries no message must still say so, never log an empty reason");
  }

  // ===== Helpers =====

  private static async Task<CapturingLogger> _failAsync(string? error) {
    var logger = new CapturingLogger();
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRebuilder>(new RecordingRebuilder(
      new RebuildResult("P", 0, 0, TimeSpan.Zero, Success: false, Error: error)));
    services.AddSingleton<IPerspectiveRunnerRegistry>(new OneRegistry("P"));
    await using var sp = services.BuildServiceProvider();
    var receptor = new RebuildPerspectiveCommandReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);
    await receptor.HandleAsync(new RebuildPerspectiveCommand(
      PerspectiveNames: null, Mode: RebuildMode.InPlace, IncludeStreamIds: [Guid.CreateVersion7()], ExcludeStreamIds: null), CancellationToken.None);
    return logger;
  }

  private static MessageEnvelope<OrderCreatedEvent> _envelope() => new() {
    MessageId = MessageId.From(Guid.CreateVersion7()),
    Payload = new OrderCreatedEvent { OrderId = Guid.NewGuid(), CustomerName = "Rebuild" },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTime.UtcNow,
        ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.NewGuid(), ServiceName = "test-service", HostName = "test-host", ProcessId = 1 }
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private sealed class OneRegistry(string name) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(name, $"global::Test.{name}", "global::Test.TestModel", [])];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors => new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class RecordingRebuilder(RebuildResult result) : IPerspectiveRebuilder {
    public IReadOnlyList<Guid>? LastStreamIds { get; private set; }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) {
      LastStreamIds = [.. streamIds];
      return Task.FromResult(result);
    }

    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds,
        RebuildOrigin origin, CancellationToken ct = default) => RebuildStreamsAsync(perspectiveName, streamIds, ct);

    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => Task.FromResult(result);
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) => Task.FromResult(result);
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => Task.FromResult(result);
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, RebuildOrigin origin, CancellationToken ct = default) => Task.FromResult(result);
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<RebuildStatus?>(null);
  }

  private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<RebuildPerspectiveCommandReceptor> {
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages {
      get {
        lock (_messages) {
          return [.. _messages];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_messages) {
        _messages.Add(formatter(state, exception));
      }
    }
  }
}
