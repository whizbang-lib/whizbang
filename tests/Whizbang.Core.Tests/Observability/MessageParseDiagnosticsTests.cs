// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The parse observer is a process-wide seam: a framework parse site records each deserialization of a
/// message's envelope or payload, and an observer sees it with the message id, the source, the kind and the
/// site. These tests pin the contract the parse-count measurements and their regression pins depend on:
/// nothing is reported with no observer, every observer sees every parse while subscribed and none after,
/// and a recorder filters to its own messages so concurrent tests never see each other's parses.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/MessageParseDiagnostics.cs</code-under-test>
/// <docs>operations/observability/diagnostics</docs>
[NotInParallel("MessageParseDiagnostics")]
public class MessageParseDiagnosticsTests {
  [Test]
  public async Task Record_WithNoObserver_IsNotEnabledAndReportsNothingAsync() {
    await Assert.That(MessageParseDiagnostics.IsEnabled).IsFalse()
      .Because("no observer is subscribed while this class runs, so a parse site must skip its report");

    // Nothing to observe it: the call is a no-op, which is the cheap-when-off path.
    MessageParseDiagnostics.Record(Guid.NewGuid(), MessageParseSource.InboxRead, MessageParseKind.Envelope);

    using var recorder = new MessageParseRecorder();
    await Assert.That(recorder.Parses).IsEmpty()
      .Because("a parse recorded before the recorder subscribed is not replayed to it");
  }

  [Test]
  public async Task Record_WithARecorder_CapturesIdSourceKindAndCallSiteAsync() {
    var messageId = Guid.NewGuid();
    using var recorder = new MessageParseRecorder();

    await Assert.That(MessageParseDiagnostics.IsEnabled).IsTrue();
    MessageParseDiagnostics.Record(messageId, MessageParseSource.EventStoreRead, MessageParseKind.Payload);

    await Assert.That(recorder.Parses.Count).IsEqualTo(1);
    var parse = recorder.Parses[0];
    await Assert.That(parse.MessageId).IsEqualTo(messageId);
    await Assert.That(parse.Source).IsEqualTo(MessageParseSource.EventStoreRead);
    await Assert.That(parse.Kind).IsEqualTo(MessageParseKind.Payload);
    await Assert.That(parse.Member).IsEqualTo(nameof(Record_WithARecorder_CapturesIdSourceKindAndCallSiteAsync));
    await Assert.That(parse.FilePath).EndsWith("MessageParseDiagnosticsTests.cs");
    await Assert.That(parse.Line).IsGreaterThan(0);
    await Assert.That(parse.Site).IsEqualTo($"MessageParseDiagnosticsTests.cs:{parse.Line}");
  }

  [Test]
  public async Task Recorder_WithAFilter_KeepsOnlyTheMessagesItWasGivenAsync() {
    var mine = Guid.NewGuid();
    var other = Guid.NewGuid();
    using var recorder = new MessageParseRecorder(mine);

    MessageParseDiagnostics.Record(other, MessageParseSource.InboxRead, MessageParseKind.Envelope);
    MessageParseDiagnostics.Record(mine, MessageParseSource.InboxRead, MessageParseKind.Envelope);
    MessageParseDiagnostics.Record(mine, MessageParseSource.LifecycleStage, MessageParseKind.Payload);

    await Assert.That(recorder.Parses.Count).IsEqualTo(2);
    await Assert.That(recorder.CountFor(mine)).IsEqualTo(2);
    await Assert.That(recorder.CountFor(other)).IsEqualTo(0);
    await Assert.That(recorder.CountFor(mine, MessageParseKind.Payload)).IsEqualTo(1);
    await Assert.That(recorder.CountFor(mine, MessageParseKind.Envelope)).IsEqualTo(1);
  }

  [Test]
  public async Task Recorder_Track_AddsAMessageToTheFilterAfterConstructionAsync() {
    var first = Guid.NewGuid();
    var later = Guid.NewGuid();
    using var recorder = new MessageParseRecorder(first);

    MessageParseDiagnostics.Record(later, MessageParseSource.TransportReceive, MessageParseKind.Envelope);
    recorder.Track(later);
    MessageParseDiagnostics.Record(later, MessageParseSource.TransportReceive, MessageParseKind.Envelope);

    await Assert.That(recorder.CountFor(later)).IsEqualTo(1)
      .Because("a message is recorded from the moment it is tracked, not before");
  }

  [Test]
  public async Task Recorder_WithNoFilter_KeepsEveryMessageAsync() {
    var a = Guid.NewGuid();
    var b = Guid.NewGuid();
    using var recorder = new MessageParseRecorder();

    MessageParseDiagnostics.Record(a, MessageParseSource.OutboxRead, MessageParseKind.Envelope);
    MessageParseDiagnostics.Record(b, MessageParseSource.MultiStreamRun, MessageParseKind.Payload);

    await Assert.That(recorder.CountFor(a)).IsEqualTo(1);
    await Assert.That(recorder.CountFor(b)).IsEqualTo(1);
  }

  [Test]
  public async Task Recorder_Summarize_GroupsByKindSourceAndSiteAsync() {
    var id = Guid.NewGuid();
    using var recorder = new MessageParseRecorder(id);

    for (var i = 0; i < 3; i++) {
      MessageParseDiagnostics.Record(id, MessageParseSource.EventStoreRead, MessageParseKind.Payload);
    }
    MessageParseDiagnostics.Record(id, MessageParseSource.InboxRead, MessageParseKind.Envelope);

    var summary = recorder.Summarize(id);
    await Assert.That(summary.Count).IsEqualTo(2);
    var payload = summary.Single(s => s.Kind == MessageParseKind.Payload);
    await Assert.That(payload.Source).IsEqualTo(MessageParseSource.EventStoreRead);
    await Assert.That(payload.Count).IsEqualTo(3);
    await Assert.That(payload.Site).StartsWith("MessageParseDiagnosticsTests.cs:");
    await Assert.That(summary.Single(s => s.Kind == MessageParseKind.Envelope).Count).IsEqualTo(1);
    await Assert.That(recorder.Summarize(Guid.NewGuid())).IsEmpty();
  }

  [Test]
  public async Task Dispose_Unsubscribes_AndLaterParsesAreNotSeenAsync() {
    var id = Guid.NewGuid();
    var recorder = new MessageParseRecorder(id);
    MessageParseDiagnostics.Record(id, MessageParseSource.Redelivery, MessageParseKind.Envelope);

    recorder.Dispose();
    recorder.Dispose(); // idempotent
    MessageParseDiagnostics.Record(id, MessageParseSource.Redelivery, MessageParseKind.Envelope);

    await Assert.That(recorder.CountFor(id)).IsEqualTo(1);
    await Assert.That(MessageParseDiagnostics.IsEnabled).IsFalse();
  }

  [Test]
  public async Task Subscribe_TwoObservers_BothSeeEveryParseUntilEachLeavesAsync() {
    var id = Guid.NewGuid();
    var first = new CountingObserver();
    var second = new CountingObserver();
    var firstSubscription = MessageParseDiagnostics.Subscribe(first);
    using var secondSubscription = MessageParseDiagnostics.Subscribe(second);

    MessageParseDiagnostics.Record(id, MessageParseSource.OffloadRehydration, MessageParseKind.Envelope);
    firstSubscription.Dispose();
    firstSubscription.Dispose(); // a second dispose is a no-op and does not remove the other observer
    MessageParseDiagnostics.Record(id, MessageParseSource.OffloadRehydration, MessageParseKind.Envelope);

    await Assert.That(first.Count).IsEqualTo(1);
    await Assert.That(second.Count).IsEqualTo(2);
  }

  [Test]
  public async Task Subscribe_NullObserver_ThrowsAsync() {
    await Assert.That(() => MessageParseDiagnostics.Subscribe(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task MessageParse_Site_WithNoFilePath_IsTheMemberNameAsync() {
    var parse = new MessageParse(Guid.Empty, MessageParseSource.InboxRead, MessageParseKind.Envelope, "Member", "", 0);
    await Assert.That(parse.Site).IsEqualTo("Member");
  }

  [Test]
  public async Task MessageParse_Site_WithAWindowsPath_IsTheFileNameAndLineAsync() {
    var parse = new MessageParse(Guid.Empty, MessageParseSource.InboxRead, MessageParseKind.Envelope, "Member", @"C:\src\Worker.cs", 12);
    await Assert.That(parse.Site).IsEqualTo("Worker.cs:12");
  }

  private sealed class CountingObserver : IMessageParseObserver {
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void OnParse(in MessageParse parse) => Interlocked.Increment(ref _count);
  }
}
