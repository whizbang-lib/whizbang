#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

namespace Whizbang.Transports.Mutations;

/// <summary>
/// Provides context information during mutation execution.
/// Passed to pre/post hooks and error handlers in <see cref="MutationEndpointBase{TCommand, TResult}"/>.
/// </summary>
/// <docs>apis/mutations/hooks#context</docs>
/// <tests>tests/Whizbang.Transports.Mutations.Tests/Unit/MutationContextTests.cs</tests>
public interface IMutationContext {
  /// <summary>
  /// The cancellation token for the current request.
  /// </summary>
  CancellationToken CancellationToken { get; }

  /// <summary>
  /// A dictionary for passing custom data between hooks.
  /// Use this to share state between OnBeforeExecuteAsync and OnAfterExecuteAsync.
  /// </summary>
  IDictionary<string, object?> Items { get; }
}

/// <summary>
/// Default implementation of <see cref="IMutationContext"/>.
/// </summary>
/// <docs>apis/mutations/hooks#context</docs>
/// <tests>tests/Whizbang.Transports.Mutations.Tests/Unit/MutationContextTests.cs</tests>
/// <remarks>
/// Creates a new mutation context.
/// </remarks>
/// <param name="cancellationToken">The cancellation token for the request.</param>
public sealed class MutationContext(CancellationToken cancellationToken) : IMutationContext {

  /// <inheritdoc />
  public CancellationToken CancellationToken { get; } = cancellationToken;

  /// <inheritdoc />
  public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
}
