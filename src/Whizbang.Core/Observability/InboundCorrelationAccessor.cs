using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Carries a correlation id captured at an inbound edge (e.g. an HTTP <c>X-Correlation-ID</c> header) so the
/// first dispatch of the request adopts it instead of minting a fresh one — letting a client-supplied
/// correlation token flow through the whole message graph.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <see cref="AsyncLocal{T}"/> so it flows with the request's async context and is isolated per
/// request. It is <see langword="null"/> unless an edge middleware sets it, so dispatches with no inbound
/// correlation keep their existing "new root, new correlation" behavior.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-context</docs>
/// <tests>tests/Whizbang.Observability.Tests/InboundCorrelationAccessorTests.cs</tests>
public static class InboundCorrelationAccessor {
  private static readonly AsyncLocal<CorrelationId?> _current = new();

  /// <summary>
  /// Gets or sets the correlation id captured at the inbound edge for the current async flow.
  /// </summary>
  public static CorrelationId? Current {
    get => _current.Value;
    set => _current.Value = value;
  }
}
