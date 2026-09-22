using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests;

/// <summary>
/// The defaults an implementer inherits by not overriding them.
/// <para>
/// Each of these exists so that a driver or extension written against an earlier version of an
/// interface keeps compiling and keeps behaving sensibly. That makes them load-bearing in a way
/// that is easy to miss: callers invoke them without knowing whether the implementation opted in,
/// so each default has to be the safe reading of "this implementation does not do that" rather
/// than something a caller could mistake for a real answer.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/IStreamIdExtractor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IEventUpcaster.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/IPerspectiveSnapshotStore.cs</code-under-test>
public class InterfaceDefaultsTests {

  private sealed class MinimalExtractor : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) => null;
    public (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) => (false, false);
  }

  private sealed class MinimalUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => false;
    public IEvent Upcast(IEvent storedEvent) => storedEvent;
  }

  private sealed class MinimalSnapshotStore : IPerspectiveSnapshotStore {
    public int CreateCalls;
    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId,
        JsonDocument snapshotData, CancellationToken ct = default) {
      CreateCalls++;
      return Task.CompletedTask;
    }
    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<(Guid, JsonDocument)?>(null);

    // The rest of the required surface: present so the type compiles, not what this fixture is about.
    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default)
      => Task.FromResult<(Guid, JsonDocument)?>(null);
    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult(false);
    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  [Test]
  public async Task AnExtractorThatCannotSet_ReportsFailureRatherThanClaimingSuccessAsync() {
    // The dispatcher uses the return value to decide whether it still needs to generate an id.
    // Defaulting to true would tell it the id was written when nothing was, and the message would
    // travel without one.
    IStreamIdExtractor extractor = new MinimalExtractor();

    await Assert.That(extractor.SetStreamId(new object(), Guid.CreateVersion7())).IsFalse()
      .Because("the caller decides whether to generate an id from this answer, so a false positive "
             + "sends the message on with no stream id at all");
  }

  [Test]
  public async Task AnUpcasterThatChangesNoTypes_DeclaresEmptySourceAndTargetSetsAsync() {
    // These pair up to tell the read seam which foreign inputs to pull in. Empty means "this
    // upcaster changes no types", which is what keeps a rebuild from dragging in events it has no
    // reason to read.
    IEventUpcaster upcaster = new MinimalUpcaster();

    await Assert.That(upcaster.SourceTypes).IsEmpty();
    await Assert.That(upcaster.TargetTypes).IsEmpty()
      .Because("a non-empty default would make every rebuilt perspective pull in inputs no "
             + "upcaster actually produces");
  }

  [Test]
  public async Task ASnapshotStoreWithoutCommitSequences_FallsBackToTheLegacyWriteAsync() {
    // A driver written before commit sequences existed still has to store snapshots. The default
    // forwards to the older overload rather than dropping the write, which would silently stop
    // snapshotting for that driver.
    var store = new MinimalSnapshotStore();

    await ((IPerspectiveSnapshotStore)store).CreateSnapshotAsync(
      Guid.CreateVersion7(), "OrdersPerspective", Guid.CreateVersion7(),
      snapshotCommitSequence: 42, JsonDocument.Parse("{}"));

    await Assert.That(store.CreateCalls).IsEqualTo(1)
      .Because("forwarding is the point — a default that dropped the write would silently stop "
             + "snapshotting for any driver that has not adopted commit sequences");
  }

  [Test]
  public async Task ASnapshotStoreWithoutCommitSequences_ReportsNoSnapshotBeforeASequenceAsync() {
    // Null means "I cannot answer that question", which the caller treats as no snapshot and
    // rebuilds from the start. Returning a snapshot without knowing its sequence would rebuild
    // from the wrong point.
    IPerspectiveSnapshotStore store = new MinimalSnapshotStore();

    var found = await store.GetLatestSnapshotBeforeCommitSequenceAsync(
      Guid.CreateVersion7(), "OrdersPerspective", beforeCommitSequence: 100);

    await Assert.That(found).IsNull()
      .Because("a driver that does not track commit sequences cannot honor the bound, and guessing "
             + "would rebuild a perspective from the wrong point");
  }
}
