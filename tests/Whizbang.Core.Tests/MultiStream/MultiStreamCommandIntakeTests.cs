// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.MultiStream;

/// <summary>
/// The intake of a multi-stream command (#1358): whichever instance receives the command records one run with one row
/// per stream, and nothing else. Every instance then works its own streams from that record.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#intake</docs>
[Category("MultiStream")]
[NotInParallel("AmbientScope")]
public class MultiStreamCommandIntakeTests {
  private static readonly Guid _runId = Guid.Parse("0199c000-0000-7000-8000-000000000001");
  private static readonly Guid _streamA = Guid.Parse("0199c000-0000-7000-8000-00000000000a");
  private static readonly Guid _streamB = Guid.Parse("0199c000-0000-7000-8000-00000000000b");

  private static MultiStreamCommandIntake<ProbeMultiStreamCommand> _intake(RecordingMultiStreamStore store) =>
    new(store, NullLogger<MultiStreamCommandIntake<ProbeMultiStreamCommand>>.Instance);

  private static ProbeMultiStreamCommand _command(params Guid[] streams) => new(_runId, streams, "label-1");

  [Test]
  public async Task HandleAsync_RecordsOneRunCarryingTheCommandAndItsStreamsAsync() {
    var store = new RecordingMultiStreamStore();

    await _intake(store).HandleAsync(_command(_streamA, _streamB));

    await Assert.That(store.Recorded).Count().IsEqualTo(1);
    var run = store.Recorded[0];
    await Assert.That(run.RunId).IsEqualTo(_runId);
    await Assert.That(run.CommandType).IsEqualTo(TypeNameFormatter.Format(typeof(ProbeMultiStreamCommand)));
    await Assert.That(run.StreamIds).IsEquivalentTo([_streamA, _streamB], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    var typeInfo = (JsonTypeInfo<ProbeMultiStreamCommand>)JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(ProbeMultiStreamCommand));
    var payload = JsonSerializer.Deserialize(run.Payload, typeInfo)!;
    await Assert.That(payload.Label).IsEqualTo("label-1")
      .Because("the stored payload is the command itself, so the handler on another instance sees what the caller sent");
  }

  /// <summary>
  /// The stored payload does not carry the stream ids: they are recorded one per row, and each stream's handler gets its
  /// stream id as a parameter. A payload listing every stream was read and parsed once per stream, so a run cost grew
  /// with the square of its stream count; a 127,288-stream rebuild read about 630 GB and ran its host out of memory.
  /// </summary>
  [Test]
  public async Task HandleAsync_StoresAPayloadWhoseSizeDoesNotDependOnTheStreamCountAsync() {
    var one = new RecordingMultiStreamStore();
    var many = new RecordingMultiStreamStore();
    var tenThousand = Enumerable.Range(1, 10_000).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();

    await _intake(one).HandleAsync(_command(_streamA));
    await _intake(many).HandleAsync(_command(tenThousand));

    await Assert.That(many.Recorded[0].StreamIds.Count).IsEqualTo(10_000)
      .Because("every stream is still recorded, one per row");
    await Assert.That(many.Recorded[0].Payload.Length).IsEqualTo(one.Recorded[0].Payload.Length)
      .Because("the payload is the command without its streams, the same size for one stream or ten thousand");
  }

  /// <summary>The payload stored without its streams still reads back into the command with everything else intact.</summary>
  [Test]
  public async Task HandleAsync_ThePayloadWithoutItsStreams_ReadsBackIntoTheCommandAsync() {
    var store = new RecordingMultiStreamStore();

    await _intake(store).HandleAsync(_command(_streamA, _streamB));

    var typeInfo = (JsonTypeInfo<ProbeMultiStreamCommand>)JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(ProbeMultiStreamCommand));
    var payload = JsonSerializer.Deserialize(store.Recorded[0].Payload, typeInfo)!;
    await Assert.That(payload.RunId).IsEqualTo(_runId);
    await Assert.That(payload.Label).IsEqualTo("label-1");
    await Assert.That(payload.StreamIds).IsEmpty()
      .Because("the streams are rows of the run, not part of the payload each stream reads");
  }

  [Test]
  public async Task HandleAsync_AStreamNamedTwice_IsRecordedOnceInFirstOccurrenceOrderAsync() {
    var store = new RecordingMultiStreamStore();

    await _intake(store).HandleAsync(_command(_streamB, _streamA, _streamB));

    await Assert.That(store.Recorded[0].StreamIds).IsEquivalentTo([_streamB, _streamA], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a stream is worked once per run however often the caller named it");
  }

  [Test]
  public async Task HandleAsync_AnEmptyStreamList_IsRefusedAndRecordsNothingAsync() {
    var store = new RecordingMultiStreamStore();

    await Assert.That(async () => await _intake(store).HandleAsync(_command())).Throws<ArgumentException>();

    await Assert.That(store.Recorded).IsEmpty()
      .Because("an empty list is a mistake, never a request for every stream (#1356)");
  }

  [Test]
  public async Task HandleAsync_NoRunId_IsRefusedAndRecordsNothingAsync() {
    var store = new RecordingMultiStreamStore();

    await Assert.That(async () => await _intake(store).HandleAsync(new ProbeMultiStreamCommand(Guid.Empty, [_streamA], "x")))
      .Throws<ArgumentException>();

    await Assert.That(store.Recorded).IsEmpty()
      .Because("the run id makes a redelivery idempotent and lets the caller find the run, so one must be given");
  }

  [Test]
  public async Task HandleAsync_AnEmptyStreamId_IsRefusedAndRecordsNothingAsync() {
    var store = new RecordingMultiStreamStore();

    await Assert.That(async () => await _intake(store).HandleAsync(_command(_streamA, Guid.Empty))).Throws<ArgumentException>();

    await Assert.That(store.Recorded).IsEmpty();
  }

  [Test]
  public async Task HandleAsync_NoStreamList_IsRefusedAndRecordsNothingAsync() {
    var store = new RecordingMultiStreamStore();

    await Assert.That(async () => await _intake(store).HandleAsync(new ProbeMultiStreamCommand(_runId, null!, "x")))
      .Throws<ArgumentException>();

    await Assert.That(store.Recorded).IsEmpty();
  }

  [Test]
  public async Task Constructor_NoStore_ThrowsAsync() {
    await Assert.That(() => new MultiStreamCommandIntake<ProbeMultiStreamCommand>(
      null!, NullLogger<MultiStreamCommandIntake<ProbeMultiStreamCommand>>.Instance)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NoLogger_ThrowsAsync() {
    await Assert.That(() => new MultiStreamCommandIntake<ProbeMultiStreamCommand>(new RecordingMultiStreamStore(), null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task NullStore_IsNotConfiguredAndRefusesToRecordAsync() {
    var store = NullMultiStreamCommandStore.Instance;

    await Assert.That(store.IsConfigured).IsFalse();
    await Assert.That(async () => await store.RecordRunAsync(new MultiStreamRun(_runId, "t", "{}", [_streamA], null), CancellationToken.None))
      .Throws<InvalidOperationException>();
    await Assert.That(async () => await store.ClaimAsync(new MultiStreamClaim(_runId, 1, TimeSpan.FromMinutes(1), null), CancellationToken.None))
      .Throws<InvalidOperationException>();
    await Assert.That(async () => await store.CompleteAsync(_runId, [], 1, TimeSpan.FromMinutes(1), CancellationToken.None)).Throws<InvalidOperationException>();
    await Assert.That(async () => await store.ReadRunAsync(_runId, CancellationToken.None)).Throws<InvalidOperationException>();
    await Assert.That(async () => await store.PurgeCompletedAsync(DateTimeOffset.UnixEpoch, CancellationToken.None)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task HandleAsync_NoCommand_ThrowsAsync() {
    var store = new RecordingMultiStreamStore();

    await Assert.That(async () => await _intake(store).HandleAsync(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task HandleAsync_NoStoreRegistered_ThrowsInsteadOfDroppingTheCommandAsync() {
    var intake = new MultiStreamCommandIntake<ProbeMultiStreamCommand>(
      NullMultiStreamCommandStore.Instance, NullLogger<MultiStreamCommandIntake<ProbeMultiStreamCommand>>.Instance);

    await Assert.That(async () => await intake.HandleAsync(_command(_streamA))).Throws<InvalidOperationException>()
      .Because("without a store nothing would ever run, and the caller must not be told the command was accepted");
  }

  [Test]
  public async Task HandleAsync_ARunAlreadyRecorded_IsAcceptedWithoutRecordingAgainAsync() {
    var store = new RecordingMultiStreamStore { AlreadyRecorded = true };

    await _intake(store).HandleAsync(_command(_streamA));

    await Assert.That(store.Recorded).Count().IsEqualTo(1)
      .Because("a redelivered command reaches the store, which keeps the first recording and reports it");
  }

  [Test]
  public async Task HandleAsync_UnderAnAmbientScope_RecordsThatScopeForTheHandlersAsync() {
    var store = new RecordingMultiStreamStore();
    var previous = ScopeContextAccessor.CurrentContext;
    ScopeContextAccessor.CurrentContext = new ScopeContext {
      Scope = new PerspectiveScope { TenantId = "tenant-a", UserId = "user-a" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    try {
      await _intake(store).HandleAsync(_command(_streamA));
    } finally {
      ScopeContextAccessor.CurrentContext = previous;
    }

    var scope = MultiStreamScope.Restore(store.Recorded[0].Scope);
    await Assert.That(scope).IsNotNull();
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-a")
      .Because("each stream's handler runs later, on another instance, under the scope the command was sent with");
    await Assert.That(scope.Scope.UserId).IsEqualTo("user-a");
  }

  [Test]
  public async Task HandleAsync_WithNoAmbientScope_RecordsNoScopeAsync() {
    var store = new RecordingMultiStreamStore();
    var previous = ScopeContextAccessor.CurrentContext;
    ScopeContextAccessor.CurrentContext = null;
    try {
      await _intake(store).HandleAsync(_command(_streamA));
    } finally {
      ScopeContextAccessor.CurrentContext = previous;
    }

    await Assert.That(store.Recorded[0].Scope).IsNull();
  }
}
