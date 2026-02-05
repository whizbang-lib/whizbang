using Whizbang.Core;

namespace Whizbang.Transports.Mutations;

/// <summary>
/// Base class for mutation endpoints with pre/post execution hooks.
/// Transport-specific implementations (FastEndpoints, HotChocolate) inherit from this
/// to provide consistent hook behavior across all transports.
/// Generated endpoint classes are partial, allowing users to override hooks.
/// </summary>
/// <typeparam name="TCommand">The command type that implements <see cref="ICommand"/>.</typeparam>
/// <typeparam name="TResult">The result type returned after command execution.</typeparam>
/// <docs>v0.1.0/mutations/hooks</docs>
/// <tests>tests/Whizbang.Transports.Mutations.Tests/Unit/MutationEndpointBaseTests.cs</tests>
/// <example>
/// // User's partial class for customization
/// public partial class CreateOrderEndpoint {
///     protected override async ValueTask OnBeforeExecuteAsync(
///         CreateOrderCommand command,
///         IMutationContext context,
///         CancellationToken ct) {
///         await _validator.ValidateAndThrowAsync(command, ct);
///     }
///
///     protected override async ValueTask OnAfterExecuteAsync(
///         CreateOrderCommand command,
///         OrderResult result,
///         IMutationContext context,
///         CancellationToken ct) {
///         await _notificationService.NotifyAsync(result.OrderId, ct);
///     }
/// }
/// </example>
public abstract class MutationEndpointBase<TCommand, TResult>
    where TCommand : ICommand {
  /// <summary>
  /// Hook called before command dispatch.
  /// Override to add validation, logging, authorization, or other pre-processing.
  /// </summary>
  /// <param name="command">The command to be executed.</param>
  /// <param name="context">The mutation context with cancellation token and shared items.</param>
  /// <param name="ct">The cancellation token.</param>
  /// <returns>A <see cref="ValueTask"/> that completes when pre-processing is done.</returns>
  /// <docs>v0.1.0/mutations/hooks#before</docs>
  protected virtual ValueTask OnBeforeExecuteAsync(
      TCommand command,
      IMutationContext context,
      CancellationToken ct) => ValueTask.CompletedTask;

  /// <summary>
  /// Hook called after successful command dispatch.
  /// Override to add post-processing, notifications, or audit logging.
  /// Not called if dispatch throws an exception.
  /// </summary>
  /// <param name="command">The executed command.</param>
  /// <param name="result">The result from command execution.</param>
  /// <param name="context">The mutation context with cancellation token and shared items.</param>
  /// <param name="ct">The cancellation token.</param>
  /// <returns>A <see cref="ValueTask"/> that completes when post-processing is done.</returns>
  /// <docs>v0.1.0/mutations/hooks#after</docs>
  protected virtual ValueTask OnAfterExecuteAsync(
      TCommand command,
      TResult result,
      IMutationContext context,
      CancellationToken ct) => ValueTask.CompletedTask;

  /// <summary>
  /// Hook called when command dispatch throws an exception.
  /// Override to provide custom error handling, logging, or fallback results.
  /// Return null to rethrow the exception, or return a result to suppress it.
  /// </summary>
  /// <param name="command">The command that caused the error.</param>
  /// <param name="ex">The exception that was thrown.</param>
  /// <param name="context">The mutation context with cancellation token and shared items.</param>
  /// <param name="ct">The cancellation token.</param>
  /// <returns>
  /// A result to return instead of throwing, or null to rethrow the exception.
  /// </returns>
  /// <docs>v0.1.0/mutations/hooks#error</docs>
  protected virtual ValueTask<TResult?> OnErrorAsync(
      TCommand command,
      Exception ex,
      IMutationContext context,
      CancellationToken ct) => ValueTask.FromResult<TResult?>(default);

  /// <summary>
  /// Maps a custom request DTO to the command type.
  /// Override this method when using <see cref="CommandEndpointAttribute{TCommand, TResult}.RequestType"/>.
  /// </summary>
  /// <typeparam name="TRequest">The request DTO type.</typeparam>
  /// <param name="request">The incoming request.</param>
  /// <param name="ct">The cancellation token.</param>
  /// <returns>The command created from the request.</returns>
  /// <exception cref="NotImplementedException">
  /// Thrown when RequestType is specified but this method is not overridden.
  /// </exception>
  /// <docs>v0.1.0/mutations/custom-request-dto#mapping</docs>
  protected virtual ValueTask<TCommand> MapRequestToCommandAsync<TRequest>(
      TRequest request,
      CancellationToken ct) where TRequest : notnull {
    throw new NotImplementedException(
        $"When using a custom RequestType, you must override MapRequestToCommandAsync " +
        $"in your partial class to map {typeof(TRequest).Name} to {typeof(TCommand).Name}.");
  }

  /// <summary>
  /// Dispatches the command to the handler.
  /// Override in transport-specific implementations to use IDispatcher or direct invocation.
  /// </summary>
  /// <param name="command">The command to dispatch.</param>
  /// <param name="ct">The cancellation token.</param>
  /// <returns>The result from the command handler.</returns>
  /// <docs>v0.1.0/mutations/command-endpoints#dispatch</docs>
  protected abstract ValueTask<TResult> DispatchCommandAsync(
      TCommand command,
      CancellationToken ct);

  /// <summary>
  /// Executes the mutation with the full hook lifecycle:
  /// 1. Check cancellation
  /// 2. OnBeforeExecuteAsync
  /// 3. DispatchCommandAsync
  /// 4. OnAfterExecuteAsync (on success) or OnErrorAsync (on failure)
  /// </summary>
  /// <param name="command">The command to execute.</param>
  /// <param name="ct">The cancellation token.</param>
  /// <returns>The result from command execution.</returns>
  /// <docs>v0.1.0/mutations/hooks#lifecycle</docs>
  protected async ValueTask<TResult> ExecuteAsync(TCommand command, CancellationToken ct) {
    ct.ThrowIfCancellationRequested();

    var context = new MutationContext(ct);

    await OnBeforeExecuteAsync(command, context, ct);

    TResult result;
    try {
      result = await DispatchCommandAsync(command, ct);
    } catch (Exception ex) {
      var errorResult = await OnErrorAsync(command, ex, context, ct);
      if (errorResult is null) {
        throw;
      }
      return errorResult;
    }

    await OnAfterExecuteAsync(command, result, context, ct);

    return result;
  }

  /// <summary>
  /// Executes the mutation using a custom request DTO.
  /// Maps the request to a command, then executes with the full hook lifecycle.
  /// </summary>
  /// <typeparam name="TRequest">The request DTO type.</typeparam>
  /// <param name="request">The incoming request.</param>
  /// <param name="ct">The cancellation token.</param>
  /// <returns>The result from command execution.</returns>
  /// <docs>v0.1.0/mutations/custom-request-dto#execution</docs>
  protected async ValueTask<TResult> ExecuteWithRequestAsync<TRequest>(
      TRequest request,
      CancellationToken ct) where TRequest : notnull {
    var command = await MapRequestToCommandAsync(request, ct);
    return await ExecuteAsync(command, ct);
  }
}
