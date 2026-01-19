using System;
using System.Threading;
using System.Threading.Tasks;
using ECommerce.Contracts.Events;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Test receptor that fires at PostDistributeInline stage to test lifecycle deserialization.
/// This receptor tests the code path where messages are deserialized from MessageEnvelope&lt;JsonElement&gt;
/// using the stored EnvelopeType field.
/// </summary>
[FireAt(LifecycleStage.PostDistributeInline)]
public sealed class DistributeStageTestReceptor : IReceptor<ProductCreatedEvent>, IAcceptsLifecycleContext {
  private readonly TaskCompletionSource<ProductCreatedEvent> _completionSource;
  private ILifecycleContext? _context;

  public DistributeStageTestReceptor(TaskCompletionSource<ProductCreatedEvent> completionSource) {
    _completionSource = completionSource ?? throw new ArgumentNullException(nameof(completionSource));
  }

  public void SetLifecycleContext(ILifecycleContext context) {
    _context = context;
  }

  public ValueTask HandleAsync(ProductCreatedEvent message, CancellationToken cancellationToken = default) {
    Console.WriteLine($"[DistributeStageTestReceptor] Received ProductCreatedEvent at {_context?.CurrentStage}");
    Console.WriteLine($"[DistributeStageTestReceptor] ProductId: {message.ProductId}");
    Console.WriteLine($"[DistributeStageTestReceptor] Name: {message.Name}");

    // Signal completion with the deserialized event
    _completionSource.TrySetResult(message);

    return ValueTask.CompletedTask;
  }
}
