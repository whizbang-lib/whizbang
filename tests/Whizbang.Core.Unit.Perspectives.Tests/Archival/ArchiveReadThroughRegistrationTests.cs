// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// The archive seam is composed into the event store every driver registers (#1738): innermost in the decorator chain
/// whenever an <see cref="IArchiveStore"/> is registered, honoring <c>Whizbang:Archive:ReadThrough</c>, and absent when
/// none is, so a host without an archive pays nothing.
/// </summary>
/// <docs>fundamentals/events/archive-store#read-through</docs>
/// <code-under-test>src/Whizbang.Core/ServiceCollectionExtensions.cs</code-under-test>
public class ArchiveReadThroughRegistrationTests {
  private static readonly Guid _stream = Guid.Parse("0199f0a1-0000-7000-8000-0000000000bb");
  private static readonly IReadOnlyList<Type> _types = [typeof(ArchiveProbeEvent)];

  [Test]
  public async Task WithAnArchiveStore_TheDecoratedStore_ReadsThroughTheArchive_AndRestoresOnAppendAsync() {
    var (hot, archive) = _fakes();
    hot.AppendFailures.Enqueue(new StreamArchivedException("x", _stream, 2, AppendToArchivedPolicy.AutoRestore));
    await using var provider = _compose(hot, archive, readThrough: null);
    await using var scope = provider.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

    var read = await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types));
    await store.AppendAsync(_stream, new ArchiveProbeEvent { N = 9 });

    await Assert.That(read.Select(e => ((ArchiveProbeEvent)e.Payload).N)).IsEquivalentTo([1, 2, 3])
      .Because("read-through is on by default, so the archived versions come back ahead of the hot one");
    await Assert.That(archive.Restores).IsEquivalentTo([_stream]);
  }

  [Test]
  public async Task WithReadThroughOff_TheDecoratedStore_ReadsOnlyHotEvents_ButStillRestoresOnAppendAsync() {
    var (hot, archive) = _fakes();
    hot.AppendFailures.Enqueue(new StreamArchivedException("x", _stream, 2, AppendToArchivedPolicy.AutoRestore));
    await using var provider = _compose(hot, archive, readThrough: "false");
    await using var scope = provider.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

    var read = await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types));
    await store.AppendAsync(_stream, new ArchiveProbeEvent { N = 9 });

    await Assert.That(read.Select(e => ((ArchiveProbeEvent)e.Payload).N)).IsEquivalentTo([3]);
    await Assert.That(archive.Restores).IsEquivalentTo([_stream])
      .Because("the append policy is the stream type's, whatever the read setting says");
  }

  [Test]
  public async Task WithoutAnArchiveStore_TheStore_IsNotDecoratedAsync() {
    var (hot, _) = _fakes();
    hot.AppendFailures.Enqueue(new StreamArchivedException("x", _stream, 2, AppendToArchivedPolicy.AutoRestore));
    await using var provider = _compose(hot, archive: null, readThrough: null);
    await using var scope = provider.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

    var read = await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types));

    await Assert.That(read.Select(e => ((ArchiveProbeEvent)e.Payload).N)).IsEquivalentTo([3]);
    await Assert.That(async () => await store.AppendAsync(_stream, new ArchiveProbeEvent { N = 9 })).Throws<StreamArchivedException>()
      .Because("with no archive store there is nothing to restore through");
  }

  private static (ArchiveAwareEventStoreDecoratorTests.HotStore Hot, ArchiveAwareEventStoreDecoratorTests.ArchiveFake Archive) _fakes() {
    var archive = new ArchiveAwareEventStoreDecoratorTests.ArchiveFake();
    archive.Add(_stream, 1, 2);
    var hot = new ArchiveAwareEventStoreDecoratorTests.HotStore();
    hot.Add(_stream, 3, 3);
    return (hot, archive);
  }

  private static ServiceProvider _compose(IEventStore hot, IArchiveStore? archive, string? readThrough) {
    var services = new ServiceCollection();
    services.AddLogging();
    var settings = readThrough is null
      ? Array.Empty<KeyValuePair<string, string?>>()
      : [new KeyValuePair<string, string?>("Whizbang:Archive:ReadThrough", readThrough)];
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    services.AddScoped(_ => hot);
    // The append-and-wait decorator resolves the sync awaiter, which needs a coordinator; nothing here calls it.
    services.AddSingleton<IWorkCoordinator, IdleCoordinator>();
    if (archive is not null) {
      services.AddSingleton(archive);
    }
    services.DecorateEventStoreWithSyncTracking();
    return services.BuildServiceProvider();
  }

  /// <summary>A coordinator the composed store resolves and never uses.</summary>
  private sealed class IdleCoordinator : IWorkCoordinator {
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private static async Task<List<T>> _listAsync<T>(IAsyncEnumerable<T> source) {
    var list = new List<T>();
    await foreach (var item in source) {
      list.Add(item);
    }
    return list;
  }
}
