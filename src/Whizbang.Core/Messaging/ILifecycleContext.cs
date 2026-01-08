using System;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Provides contextual information about the current lifecycle stage invocation.
/// Receptors can optionally inject this interface to access metadata about when and why they're being invoked.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ILifecycleContext"/> is optional - receptors only need to inject it if they require
/// context information. Receptors that don't need context can simply omit it from their constructor.
/// </para>
/// <para>
/// <strong>Example:</strong> Receptor that filters by perspective name:
/// </para>
/// <code>
/// [FireAt(LifecycleStage.PostPerspectiveInline)]
/// public class ProductCatalogCompletionReceptor : IReceptor&lt;ProductCreatedEvent&gt; {
///   private readonly ILifecycleContext? _context;
///   private readonly TaskCompletionSource&lt;bool&gt; _completion;
///
///   public ProductCatalogCompletionReceptor(
///       TaskCompletionSource&lt;bool&gt; completion,
///       ILifecycleContext? context = null) {
///     _completion = completion;
///     _context = context;
///   }
///
///   public ValueTask HandleAsync(ProductCreatedEvent evt, CancellationToken ct) {
///     // Only signal completion if ProductCatalog perspective processed this
///     if (_context?.PerspectiveName == "ProductCatalog") {
///       _completion.SetResult(true);
///     }
///     return ValueTask.CompletedTask;
///   }
/// }
/// </code>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
public interface ILifecycleContext {
  /// <summary>
  /// Gets the current lifecycle stage at which the receptor is being invoked.
  /// </summary>
  LifecycleStage CurrentStage { get; }

  /// <summary>
  /// Gets the event ID that triggered this lifecycle invocation, if applicable.
  /// Null for lifecycle stages that don't process specific events (e.g., ImmediateAsync).
  /// </summary>
  Guid? EventId { get; }

  /// <summary>
  /// Gets the stream ID being processed, if applicable.
  /// Set for perspective, outbox, and inbox stages. Null for immediate dispatch.
  /// </summary>
  Guid? StreamId { get; }

  /// <summary>
  /// Gets the perspective name being processed, if applicable.
  /// Only set for perspective lifecycle stages (PrePerspective*, PostPerspective*).
  /// Null for other stages.
  /// </summary>
  string? PerspectiveName { get; }

  /// <summary>
  /// Gets the last successfully processed event ID (checkpoint), if applicable.
  /// Only set for perspective lifecycle stages. Represents the checkpoint position
  /// after processing completes.
  /// </summary>
  Guid? LastProcessedEventId { get; }
}
