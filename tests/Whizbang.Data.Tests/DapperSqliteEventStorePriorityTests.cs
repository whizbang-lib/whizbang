using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Priority;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Sqlite;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Tests;

/// <summary>
/// Priority step 1 through the SQLite event store: the store persists the whole typed envelope as JSON, so the
/// number rides the typed envelope metadata (<c>pri</c>) into the row and back out of a typed read. A raw append
/// made while handling other work is wrapped with that work's number (the ambient parent); an envelope appended
/// with its own number reads back with it; a raw append outside any handling reads back undeclared.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#on-the-wire</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Sqlite/DapperSqliteEventStore.cs</code-under-test>
public class DapperSqliteEventStorePriorityTests : IDisposable {
  private DapperTestBase _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  [After(Test)]
  public void Cleanup() {
    _testBase?.Cleanup();
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().Wait();
    GC.SuppressFinalize(this);
  }

  private DapperSqliteEventStore _store() =>
    new(_testBase.ConnectionFactory, _testBase.Executor, JsonOptionsHelper.CreateOptions(), new PolicyEngine());

  private static async Task<MessageEnvelope<TestEvent>> _firstAsync(DapperSqliteEventStore store, Guid streamId) {
    await foreach (var envelope in store.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      return envelope;
    }
    throw new InvalidOperationException("Test setup: the stream is empty.");
  }

  [Test]
  public async Task AppendAsync_WithMessage_WhileHandlingBackgroundWork_ReadsBackTheAmbientParentAsync() {
    var store = _store();
    var streamId = Guid.NewGuid();

    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      await store.AppendAsync(streamId, new TestEvent { StreamId = streamId, Payload = "raw" });
    }

    var stored = await _firstAsync(store, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the wrap declares from the handling in progress and the typed envelope's JSON carries it; a typed read is the far side of both");
  }

  [Test]
  public async Task AppendAsync_WithMessage_OutsideAnyHandling_ReadsBackUndeclaredAsync() {
    var store = _store();
    var streamId = Guid.NewGuid();

    await store.AppendAsync(streamId, new TestEvent { StreamId = streamId, Payload = "raw" });

    var stored = await _firstAsync(store, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("nothing to inherit means nobody said, and the row records exactly that");
  }

  [Test]
  public async Task AppendAsync_WithAnEnvelopeCarryingANumber_ReadsItBackAsync() {
    var store = _store();
    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent { StreamId = streamId, Payload = "declared" },
      Hops = [new MessageHop { Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Priority = WorkPriority.INTERACTIVE,
    };

    await store.AppendAsync(streamId, envelope);

    var stored = await _firstAsync(store, streamId);
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the typed envelope's generated JSON metadata must name pri, or every typed envelope this store keeps comes back undeclared");
  }

  private sealed class TestFixture : DapperTestBase;
}
