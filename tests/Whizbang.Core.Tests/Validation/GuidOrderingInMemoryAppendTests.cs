// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Validation;

/// <summary>
/// The in-memory event store's append runs the time-ordering check before it stores anything (#2528): a non-v7 event id
/// is refused and the stream stays empty; a non-v7 stream id is accepted and reported, or refused when stream ids are
/// raised to Error.
/// </summary>
[Category("Validation")]
[Category("GuidOrdering")]
public class GuidOrderingInMemoryAppendTests {
  private sealed record Probe(Guid Id) : IEvent;

  private static MessageEnvelope<Probe> _envelope(Guid id) => new() {
    MessageId = new MessageId(id),
    Payload = new Probe(id),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = Dispatch.DispatchModes.Local, Source = MessageSource.Local },
  };

  [Test]
  [MethodDataSource(typeof(GuidOrderingEntryPointValidationTests), nameof(GuidOrderingEntryPointValidationTests.NonV7Ids))]
  public async Task Append_NonV7EventId_IsRefusedAndTheStreamStaysEmptyAsync(Guid id, int version) {
    var store = new InMemoryEventStore();
    var stream = (Guid)TrackedGuid.New();

    var thrown = await Assert.That(async () => await store.AppendAsync(stream, _envelope(id))).Throws<GuidOrderingException>();

    await Assert.That(thrown!.EntryPoint).IsEqualTo("InMemoryEventStore.Append");
    await Assert.That(thrown.MessageType).IsEqualTo(nameof(Probe));
    await Assert.That(thrown.Version).IsEqualTo(version);
    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(-1L).Because("nothing was stored");
  }

  [Test]
  public async Task Append_V7_IsStoredAsync() {
    var store = new InMemoryEventStore();
    var stream = (Guid)TrackedGuid.New();

    await store.AppendAsync(stream, _envelope(TrackedGuid.New()));

    await Assert.That(await store.GetLastSequenceAsync(stream)).IsGreaterThanOrEqualTo(0L);
  }

  [Test]
  public async Task Append_NonV7StreamId_RaisedToError_IsRefusedAsync() {
    var options = new WhizbangOptions { StreamIdOrderingViolationSeverity = GuidOrderingSeverity.Error };
    var store = new InMemoryEventStore { GuidOrdering = new GuidOrderingValidator(options, NullLogger.Instance) };
    var stream = Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8");

    await Assert.That(async () => await store.AppendAsync(stream, _envelope(TrackedGuid.New()))).Throws<GuidOrderingException>();
    await Assert.That(await store.GetLastSequenceAsync(stream)).IsEqualTo(-1L);
  }

  /// <summary>
  /// A stream id typed by an id whose <c>Versions</c> list holds its version is stored at Error, and one it does not
  /// list is refused (#2516).
  /// </summary>
  [Test]
  public async Task Append_AStreamIdItsTypeLists_IsStoredAtError_AndOneItDoesNotIsRefusedAsync() {
    var options = new WhizbangOptions { StreamIdOrderingViolationSeverity = GuidOrderingSeverity.Error };
    var store = new InMemoryEventStore { GuidOrdering = new GuidOrderingValidator(options, NullLogger.Instance) };
    var listed = NonTimeOrderedGuidShapes.V5;
    var unlisted = NonTimeOrderedGuidShapes.V4;

    await store.AppendAsync(listed, _keyed(listed));
    await Assert.That(async () => await store.AppendAsync(unlisted, _keyed(unlisted))).Throws<GuidOrderingException>();

    await Assert.That(await store.GetLastSequenceAsync(listed)).IsGreaterThanOrEqualTo(0L);
    await Assert.That(await store.GetLastSequenceAsync(unlisted)).IsEqualTo(-1L);
  }

  private static MessageEnvelope<VersionsKeyedProbe> _keyed(Guid stream) {
    var id = (Guid)TrackedGuid.New();
    return new() {
      MessageId = new MessageId(id),
      Payload = new VersionsKeyedProbe(new Whizbang.Core.Tests.ValueObjects.MintedOrNamedTestId(stream)),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Dispatch.DispatchModes.Local, Source = MessageSource.Local },
    };
  }

  [Test]
  public async Task Append_NonV7StreamId_ByDefault_IsStoredAsync() {
    var store = new InMemoryEventStore();
    var stream = Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8");

    await store.AppendAsync(stream, _envelope(TrackedGuid.New()));

    await Assert.That(await store.GetLastSequenceAsync(stream)).IsGreaterThanOrEqualTo(0L);
  }

  [Test]
  public async Task ValidateAppend_ANullEnvelope_ThrowsAsync() {
    await Assert.That(() => GuidOrderingValidator.Default.ValidateAppend<Probe>(Guid.Empty, null!, "x"))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ValidateAppend_ANullPayload_IsNamedByTheDeclaredTypeAsync() {
    var envelope = new MessageEnvelope<Probe?> {
#pragma warning disable WHIZ037 // a v4 on purpose: the refusal is the subject
      MessageId = new MessageId(Guid.Parse("919108f7-52d1-4320-9bac-f847db4148a8")),
#pragma warning restore WHIZ037
      Payload = null,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Dispatch.DispatchModes.Local, Source = MessageSource.Local },
    };

    var thrown = Assert.Throws<GuidOrderingException>(() => GuidOrderingValidator.Default.ValidateAppend((Guid)TrackedGuid.New(), envelope, "x"));

    await Assert.That(thrown.MessageType).IsEqualTo(nameof(Probe));
  }
}

/// <summary>An event whose stream id is a strong id declared with a <c>Versions</c> list (v7 or v5, #2516).</summary>
/// <param name="StreamId">The stream.</param>
public sealed record VersionsKeyedProbe([property: StreamId] Whizbang.Core.Tests.ValueObjects.MintedOrNamedTestId StreamId) : IEvent;
