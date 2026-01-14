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
///     if (_context?.PerspectiveType?.Name == "ProductCatalogPerspective") {
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
  /// Gets the perspective type being processed, if applicable.
  /// Only set for perspective lifecycle stages (PrePerspective*, PostPerspective*).
  /// Null for other stages.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <strong>Use Case:</strong> Provides the actual <see cref="Type"/> of the perspective
  /// class being executed, allowing receptors to precisely identify which perspective
  /// triggered the lifecycle stage.
  /// </para>
  /// <code>
  /// // Filter by specific perspective type
  /// if (_context?.PerspectiveType == typeof(ProductCatalogPerspective)) {
  ///   // Only handle ProductCatalogPerspective
  /// }
  ///
  /// // Or filter by perspective name
  /// if (_context?.PerspectiveType?.Name == "ProductCatalogPerspective") {
  ///   // Only handle ProductCatalogPerspective
  /// }
  /// </code>
  /// </remarks>
  Type? PerspectiveType { get; }

  /// <summary>
  /// Gets the last successfully processed event ID (checkpoint), if applicable.
  /// Only set for perspective lifecycle stages. Represents the checkpoint position
  /// after processing completes.
  /// </summary>
  Guid? LastProcessedEventId { get; }

  /// <summary>
  /// Gets the message source (Outbox for local publish, Inbox for transport receive).
  /// Allows receptors to distinguish between local publication and external consumption.
  /// Only set for Distribute lifecycle stages (PreDistribute, Distribute, PostDistribute).
  /// </summary>
  /// <remarks>
  /// <para>
  /// <strong>Use Case:</strong> Distribute stages fire for BOTH outbox (publishing) and inbox (consuming).
  /// Receptors can filter by MessageSource to only handle one scenario:
  /// </para>
  /// <code>
  /// if (_context?.MessageSource == MessageSource.Inbox) {
  ///   return; // Skip inbox, only process outbox
  /// }
  /// </code>
  /// </remarks>
  MessageSource? MessageSource { get; }

  /// <summary>
  /// Gets the current attempt number for this work item (1-based).
  /// Increments on retries after failures. Null if not applicable for the lifecycle stage.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <strong>Use Case:</strong> Perspective lifecycle stages may fire multiple times if
  /// perspective processing succeeds but checkpoint save fails. Receptors can check
  /// AttemptNumber to only fire once:
  /// </para>
  /// <code>
  /// if (_context?.AttemptNumber > 1) {
  ///   return; // Skip retries, only fire on first attempt
  /// }
  /// </code>
  /// </remarks>
  int? AttemptNumber { get; }
}
