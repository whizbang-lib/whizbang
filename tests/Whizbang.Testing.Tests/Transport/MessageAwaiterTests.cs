using Whizbang.Core.Observability;
using Whizbang.Testing.Tests.TestSupport;
using Whizbang.Testing.Transport;

namespace Whizbang.Testing.Tests.Transport;

/// <summary>
/// Tests for <see cref="MessageAwaiter{TResult}"/>, <see cref="MessageIdAwaiter"/>, and
/// <see cref="CountingMessageAwaiter"/> - all driven by explicit handler invocations.
/// </summary>
public class MessageAwaiterTests {
  private static readonly TimeSpan _longTimeout = TimeSpan.FromSeconds(30);

  [Test]
  public async Task MessageAwaiter_NullExtractor_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentNullException>(
      () => _ = new MessageAwaiter<IMessageEnvelope>(null!));

    await Assert.That(ex!.ParamName).IsEqualTo("resultExtractor");
  }

  [Test]
  public async Task MessageAwaiter_HandlerExtractsResult_WaitReturnsItAsync() {
    var awaiter = new MessageAwaiter<IMessageEnvelope>(envelope => envelope);
    var envelope = EnvelopeFactory.Create("hello");

    await Assert.That(awaiter.IsCompleted).IsFalse();
    await awaiter.Handler(envelope, null, CancellationToken.None);

    var result = await awaiter.WaitAsync(_longTimeout);
    await Assert.That(ReferenceEquals(result, envelope)).IsTrue();
    await Assert.That(awaiter.IsCompleted).IsTrue();
  }

  [Test]
  public async Task MessageAwaiter_FilterRejects_MessageIsSkippedAsync() {
    var awaiter = new MessageAwaiter<IMessageEnvelope>(
      envelope => envelope,
      filter: _ => false);

    await awaiter.Handler(EnvelopeFactory.Create("filtered"), null, CancellationToken.None);

    await Assert.That(awaiter.IsCompleted).IsFalse();
  }

  [Test]
  public async Task MessageAwaiter_ExtractorReturnsNull_MessageIsSkippedAsync() {
    var awaiter = new MessageAwaiter<IMessageEnvelope>(_ => null);

    await awaiter.Handler(EnvelopeFactory.Create("ignored"), null, CancellationToken.None);

    await Assert.That(awaiter.IsCompleted).IsFalse();
  }

  [Test]
  public async Task MessageAwaiter_TrySetResult_CompletesDirectly_SecondCallReturnsFalseAsync() {
    var awaiter = new MessageAwaiter<string>(_ => null);

    var first = awaiter.TrySetResult("direct");
    var second = awaiter.TrySetResult("too-late");

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse();
    await Assert.That(await awaiter.WaitAsync(_longTimeout)).IsEqualTo("direct");
  }

  [Test]
  public async Task MessageAwaiter_SetException_WaitThrowsThatExceptionAsync() {
    var awaiter = new MessageAwaiter<string>(_ => null);
    awaiter.SetException(new InvalidOperationException("boom"));

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await awaiter.WaitAsync(_longTimeout));

    await Assert.That(ex!.Message).IsEqualTo("boom");
  }

  [Test]
  public async Task MessageAwaiter_WaitWithZeroTimeout_NoMessage_ThrowsTimeoutAsync() {
    var awaiter = new MessageAwaiter<string>(_ => null);

    var ex = await Assert.ThrowsAsync<TimeoutException>(
      async () => await awaiter.WaitAsync(TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("No message received within");
  }

  [Test]
  public async Task MessageAwaiter_HasUniqueAwaiterIdAsync() {
    var first = new MessageAwaiter<string>(_ => null);
    var second = new MessageAwaiter<string>(_ => null);

    await Assert.That(first.AwaiterId).IsNotEqualTo(Guid.Empty);
    await Assert.That(first.AwaiterId).IsNotEqualTo(second.AwaiterId);
  }

  [Test]
  public async Task MessageIdAwaiter_Handler_ReturnsMessageIdStringAsync() {
    var awaiter = new MessageIdAwaiter();
    var envelope = EnvelopeFactory.Create("with-id");

    await Assert.That(awaiter.IsCompleted).IsFalse();
    await awaiter.Handler(envelope, null, CancellationToken.None);

    var messageId = await awaiter.WaitAsync(_longTimeout);
    await Assert.That(messageId).IsEqualTo(envelope.MessageId.ToString());
    await Assert.That(awaiter.IsCompleted).IsTrue();
  }

  [Test]
  public async Task MessageIdAwaiter_WithExpectedId_IgnoresStragglers_CompletesOnMatchAsync() {
    var expected = EnvelopeFactory.Create("expected");
    var straggler = EnvelopeFactory.Create("straggler");
    var awaiter = new MessageIdAwaiter(expected.MessageId.ToString());

    // A non-matching (stale/straggler) message must NOT complete the awaiter — this is what makes a
    // correlated routing assertion deterministic instead of racing whichever message arrives first.
    await awaiter.Handler(straggler, null, CancellationToken.None);
    await Assert.That(awaiter.IsCompleted).IsFalse();

    // The awaited message completes it; WaitAsync returns that id.
    await awaiter.Handler(expected, null, CancellationToken.None);
    var received = await awaiter.WaitAsync(_longTimeout);
    await Assert.That(received).IsEqualTo(expected.MessageId.ToString());
    await Assert.That(awaiter.IsCompleted).IsTrue();
  }

  [Test]
  public async Task MessageIdAwaiter_WaitWithZeroTimeout_NoMessage_ThrowsTimeoutAsync() {
    var awaiter = new MessageIdAwaiter();

    var ex = await Assert.ThrowsAsync<TimeoutException>(
      async () => await awaiter.WaitAsync(TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("No message received within");
  }

  [Test]
  public async Task CountingMessageAwaiter_ZeroExpected_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _ = new CountingMessageAwaiter(0));

    await Assert.That(ex!.ParamName).IsEqualTo("expectedCount");
  }

  [Test]
  public async Task CountingMessageAwaiter_NegativeExpected_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _ = new CountingMessageAwaiter(-3));

    await Assert.That(ex!.ParamName).IsEqualTo("expectedCount");
  }

  [Test]
  public async Task CountingMessageAwaiter_CompletesWhenExpectedCountReachedAsync() {
    var awaiter = new CountingMessageAwaiter(3);
    var envelope = EnvelopeFactory.Create("counted");

    await awaiter.Handler(envelope, null, CancellationToken.None);
    await awaiter.Handler(envelope, null, CancellationToken.None);
    await Assert.That(awaiter.IsCompleted).IsFalse();
    await Assert.That(awaiter.ReceivedCount).IsEqualTo(2);

    await awaiter.Handler(envelope, null, CancellationToken.None);

    await Assert.That(awaiter.IsCompleted).IsTrue();
    await Assert.That(awaiter.ReceivedCount).IsEqualTo(3);
    await Assert.That(awaiter.ExpectedCount).IsEqualTo(3);
    await awaiter.WaitAsync(_longTimeout);
  }

  [Test]
  public async Task CountingMessageAwaiter_Timeout_MessageIncludesProgressAsync() {
    var awaiter = new CountingMessageAwaiter(3);
    await awaiter.Handler(EnvelopeFactory.Create("one"), null, CancellationToken.None);

    var ex = await Assert.ThrowsAsync<TimeoutException>(
      async () => await awaiter.WaitAsync(TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("Expected 3 messages but only received 1");
  }

  [Test]
  public async Task DistinctMessageIdAwaiter_EmptyExpectedSet_ThrowsAsync() {
    var ex = Assert.Throws<ArgumentException>(() => _ = new DistinctMessageIdAwaiter([]));

    await Assert.That(ex!.ParamName).IsEqualTo("expectedMessageIds");
  }

  [Test]
  public async Task DistinctMessageIdAwaiter_DuplicateDelivery_CountsOnceAndCompletesOnAllDistinctAsync() {
    var first = EnvelopeFactory.Create("first");
    var second = EnvelopeFactory.Create("second");
    var third = EnvelopeFactory.Create("third");
    var awaiter = new DistinctMessageIdAwaiter([
      first.MessageId.ToString(),
      second.MessageId.ToString(),
      third.MessageId.ToString()
    ]);

    // An at-least-once transport may redeliver a message: the duplicate must not count twice or
    // complete the awaiter before every distinct expected message has arrived.
    await awaiter.Handler(first, null, CancellationToken.None);
    await awaiter.Handler(first, null, CancellationToken.None);
    await awaiter.Handler(second, null, CancellationToken.None);
    await Assert.That(awaiter.IsCompleted).IsFalse();
    await Assert.That(awaiter.DistinctReceivedCount).IsEqualTo(2);

    await awaiter.Handler(third, null, CancellationToken.None);

    await Assert.That(awaiter.IsCompleted).IsTrue();
    await Assert.That(awaiter.DistinctReceivedCount).IsEqualTo(3);
    await Assert.That(awaiter.ExpectedCount).IsEqualTo(3);
    await awaiter.WaitAsync(_longTimeout);
  }

  [Test]
  public async Task DistinctMessageIdAwaiter_UnexpectedStraggler_IgnoredAsync() {
    var expected = EnvelopeFactory.Create("expected");
    var straggler = EnvelopeFactory.Create("straggler");
    var awaiter = new DistinctMessageIdAwaiter([expected.MessageId.ToString()]);

    // A stale message an earlier test's drain missed must neither count nor complete the awaiter.
    await awaiter.Handler(straggler, null, CancellationToken.None);
    await Assert.That(awaiter.IsCompleted).IsFalse();
    await Assert.That(awaiter.DistinctReceivedCount).IsEqualTo(0);

    await awaiter.Handler(expected, null, CancellationToken.None);

    await Assert.That(awaiter.IsCompleted).IsTrue();
    await awaiter.WaitAsync(_longTimeout);
  }

  [Test]
  public async Task DistinctMessageIdAwaiter_Timeout_MessageIncludesProgressAsync() {
    var received = EnvelopeFactory.Create("received");
    var missing = EnvelopeFactory.Create("missing");
    var awaiter = new DistinctMessageIdAwaiter([
      received.MessageId.ToString(),
      missing.MessageId.ToString()
    ]);
    await awaiter.Handler(received, null, CancellationToken.None);

    var ex = await Assert.ThrowsAsync<TimeoutException>(
      async () => await awaiter.WaitAsync(TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("Expected 2 distinct messages but only received 1");
  }
}
