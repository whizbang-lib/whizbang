using System;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tail-of-round coverage for <see cref="IMessagePublishStrategy"/>'s default interface
/// implementation of <see cref="IMessagePublishStrategy.PublishBatchAsync"/>: a strategy that
/// never opted into bulk publishing must refuse the call rather than silently doing nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IMessagePublishStrategy.cs</code-under-test>
public class IMessagePublishStrategyCoverageTests {

  /// <summary>A minimal strategy that does not override the bulk-publish default.</summary>
  private sealed class _SingleItemOnlyStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      throw new NotImplementedException("not exercised by this test");
  }

  /// <summary>
  /// The publisher worker checks <see cref="IMessagePublishStrategy.SupportsBulkPublish"/> before
  /// calling <c>PublishBatchAsync</c> — but a strategy accessed only through the interface (e.g. a
  /// misconfigured DI registration bypassing that check) must fail loudly rather than the
  /// interface's default silently accepting a batch it has no way to actually deliver.
  /// </summary>
  [Test]
  public async Task PublishBatchAsync_DefaultImplementation_ThrowsNotSupportedAsync() {
    IMessagePublishStrategy strategy = new _SingleItemOnlyStrategy();

    // async lambda, not a bare call: the assertion overload for a Func<Task<T>> infers a nullable
    // T and mismatches the interface's non-nullable IReadOnlyList return (CS8619).
    await Assert.That(async () => await strategy.PublishBatchAsync([], CancellationToken.None))
      .Throws<NotSupportedException>();
  }
}
