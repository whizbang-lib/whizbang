using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// No-batching <see cref="IInboxBatchStrategy"/> that creates a scope and flushes immediately
/// for each message. This preserves the current (pre-batching) behavior where each transport
/// handler makes its own <c>process_work_batch</c> DB call.
/// </summary>
/// <remarks>
/// Useful for:
/// <list type="bullet">
///   <item>Testing — predictable, synchronous flush behavior</item>
///   <item>Low-volume scenarios — batching adds latency when throughput is low</item>
///   <item>Debugging — eliminates batching as a variable when investigating issues</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Override before AddTransportConsumer() to disable batching:
/// services.AddSingleton&lt;IInboxBatchStrategy&gt;(sp =>
///     new ImmediateInboxBatchStrategy(sp.GetRequiredService&lt;IServiceScopeFactory&gt;()));
/// </code>
/// </example>
/// <tests>tests/Whizbang.Core.Tests/Workers/ImmediateInboxBatchStrategyTests.cs</tests>
/// <docs>messaging/transports/transport-consumer#inbox-batching</docs>
public sealed class ImmediateInboxBatchStrategy(IServiceScopeFactory scopeFactory) : IInboxBatchStrategy {
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

  /// <inheritdoc />
  public async Task<WorkBatch> EnqueueAndWaitAsync(InboxMessage message, CancellationToken ct) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var strategy = scope.ServiceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    strategy.QueueInboxMessage(message);
    return await strategy.FlushAsync(WorkBatchOptions.None, FlushMode.Required, ct);
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync() => default;
}
