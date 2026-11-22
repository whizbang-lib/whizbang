// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// The archive's provider contract (#2503): the settings a provider hands the coordinator, the failure a provider throws,
/// the selection a provider registers (explicit replaces, settings-driven only fills a gap), and the maintenance step that
/// drains the deletion queue through whichever coordinator the driver registered.
/// </summary>
/// <docs>fundamentals/events/archive-store#object-stores-and-the-coordinator</docs>
public class ArchiveObjectStoreContractTests {
  private static readonly ArchiveLocation _location = new("test", "https://account.example", "default", "archive");

  [Test]
  public async Task Settings_Defaults_AreTheMeasuredQuality_TenThousandEvents_TheCheckOn_AndNoDelayAsync() {
    var settings = new ArchiveObjectStoreSettings();

    await Assert.That(settings.CompressionLevel).IsEqualTo(4);
    await Assert.That(settings.MaxEventsPerChunk).IsEqualTo(10_000);
    await Assert.That(settings.LocationMismatch).IsEqualTo(ArchiveLocationMismatchHandling.Error);
    await Assert.That(settings.DeleteAfter).IsNull();
    await Assert.That(settings.Refusal()).IsNull();
  }

  [Test]
  public async Task Settings_RefuseWhatTheCoordinatorCannotRunWith_AndAcceptTheBoundariesAsync() {
    await Assert.That(new ArchiveObjectStoreSettings { CompressionLevel = -1 }.Refusal()).Contains("CompressionLevel is -1");
    await Assert.That(new ArchiveObjectStoreSettings { CompressionLevel = 12 }.Refusal()).Contains("0 to 11");
    await Assert.That(new ArchiveObjectStoreSettings { MaxEventsPerChunk = 0 }.Refusal()).Contains("MaxEventsPerChunk is 0");
    await Assert.That(new ArchiveObjectStoreSettings { DeleteAfter = TimeSpan.Zero }.Refusal())
      .IsEqualTo(ArchiveObjectStoreSettings.DELETE_AFTER_REFUSAL)
      .Because("a zero delay would look like protection and give none");
    var smallest = new ArchiveObjectStoreSettings { CompressionLevel = 0, MaxEventsPerChunk = 1, DeleteAfter = TimeSpan.FromTicks(1) };
    await Assert.That(smallest.Refusal()).IsNull();
    await Assert.That(new ArchiveObjectStoreSettings { CompressionLevel = 11 }.Refusal()).IsNull();
    await Assert.That(ArchiveObjectStoreSettings.RefusalForDeleteAfter(null)).IsNull();
    await Assert.That(new ArchiveObjectStoreSettings().LocationMismatch).IsEqualTo(ArchiveLocationMismatchHandling.Error)
      .Because("a mismatch stops the archive unless the service says otherwise (#2141)");
    await Assert.That(new ArchiveObjectStoreSettings { LocationMismatch = (ArchiveLocationMismatchHandling)99 }.Refusal())
      .IsEqualTo(ArchiveObjectStoreSettings.LOCATION_MISMATCH_VALUES);
    await Assert.That(new ArchiveObjectStoreSettings { LocationMismatch = ArchiveLocationMismatchHandling.Ignore }.Refusal()).IsNull();
  }

  [Test]
  public async Task StoreException_NamesTheOperationTheObjectAndTheLocation_AndKeepsTheCauseAsync() {
    var cause = new InvalidOperationException("409 conflict");

    var onObject = new ArchiveObjectStoreException("put", "s/1-2-aa.jsonl.br", _location, cause);
    var onLocation = new ArchiveObjectStoreException("list", path: null, _location, cause);

    await Assert.That(onObject.Operation).IsEqualTo("put");
    await Assert.That(onObject.Path).IsEqualTo("s/1-2-aa.jsonl.br");
    await Assert.That(onObject.Location).IsEqualTo(_location);
    await Assert.That(onObject.InnerException).IsSameReferenceAs(cause);
    await Assert.That(onObject.Message).Contains("put of object s/1-2-aa.jsonl.br").And.Contains("container 'archive'").And.Contains("409 conflict");
    await Assert.That(onLocation.Message).Contains("list of the location");
    await Assert.That(new ArchiveObjectStoreException().Operation).IsNull();
    await Assert.That(new ArchiveObjectStoreException("m").Message).IsEqualTo("m");
    await Assert.That(new ArchiveObjectStoreException("m", cause).InnerException).IsSameReferenceAs(cause);
  }

  [Test]
  public async Task Registration_ExplicitReplaces_TryAddKeepsTheFirst_AndTheStepIsAddedOnceAsync() {
    var first = new ArchiveObjectStoreSelection(new NoObjects(), "first");
    var explicitly = new ArchiveObjectStoreSelection(new NoObjects(), "code");

    var services = new ServiceCollection();
    services.TryAddWhizbangArchiveObjectStore(_ => first);
    services.TryAddWhizbangArchiveObjectStore(_ => new ArchiveObjectStoreSelection(null, "later settings"));
    await using (var provider = services.BuildServiceProvider()) {
      await Assert.That(provider.GetRequiredService<ArchiveObjectStoreSelection>()).IsSameReferenceAs(first)
        .Because("a selection made from settings never replaces one already made");
    }

    services.AddWhizbangArchiveObjectStore(_ => explicitly);
    services.TryAddWhizbangArchiveObjectStore(_ => first);
    await using var replaced = services.BuildServiceProvider();

    await Assert.That(ArchiveObjectStoreSelection.Resolve(replaced)).IsSameReferenceAs(explicitly.Store)
      .Because("an explicit registration wins, whether it comes before or after the one made from settings");
    await Assert.That(services.Count(d => d.ServiceType == typeof(IMaintenanceStep))).IsEqualTo(1)
      .Because("the deletion step is added once, however many registrations there are");
    await Assert.That(ArchiveObjectStoreSelection.Resolve(new ServiceCollection().BuildServiceProvider())).IsNull()
      .Because("with no selection, the driver keeps its table store");
  }

  [Test]
  public async Task DeletionStep_DrainsThenSweeps_AndWithoutACoordinatorDoesNothingAsync() {
    var coordinator = new RecordingCoordinator();
    var services = new ServiceCollection();
    services.TryAddWhizbangArchiveObjectStore(_ => new ArchiveObjectStoreSelection(coordinator.Objects, "code"));
    services.AddSingleton<IArchiveStore>(coordinator);
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var step = scope.ServiceProvider.GetServices<IMaintenanceStep>().Single();

    await step.RunAsync(provider, CancellationToken.None);
    await new ArchiveObjectDeletionStep(coordinator: null).RunAsync(provider, CancellationToken.None);

    await Assert.That(step.Name).IsEqualTo("archive-object-deletion");
    await Assert.That(coordinator.Calls).IsEquivalentTo([$"drain {ArchiveObjectDeletionStep.BATCH_SIZE}", "sweep-if-due"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each cycle drains a batch of the queue, then sweeps when a sweep is due; a step with no coordinator calls nothing");
    await Assert.That(async () => await step.RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  private sealed class NoObjects : IArchiveObjectStore {
    public ArchiveLocation Location => _location;
    public ArchiveObjectStoreSettings Settings { get; } = new();
    public Task PrepareAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> PutAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<byte[]> GetAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }
  }

  private sealed class RecordingCoordinator : IArchiveObjectCoordinator {
    public List<string> Calls { get; } = [];
    public IArchiveObjectStore Objects { get; } = new NoObjects();
    public ArchiveLocation ConfiguredLocation => _location;
    public Task<int> DeleteQueuedObjectsAsync(int maxObjects, CancellationToken cancellationToken = default) {
      Calls.Add($"drain {maxObjects}");
      return Task.FromResult(0);
    }
    public Task<int> SweepOrphanedObjectsIfDueAsync(CancellationToken cancellationToken = default) {
      Calls.Add("sweep-if-due");
      return Task.FromResult(0);
    }
    public Task<int> SweepOrphanedObjectsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<long> RepointLocationAsync(long fromLocationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<ArchivedEvent> ReadAsync(Guid streamId, long fromVersion = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }
}
