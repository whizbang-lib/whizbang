using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Pipeline;

/// <summary>
/// Represents a behavior in the dispatcher pipeline that can intercept message processing.
/// Behaviors execute in registration order and can:
/// - Modify the request before processing
/// - Modify the response after processing
/// - Short-circuit the pipeline (not call next)
/// - Add cross-cutting concerns (logging, validation, retry, etc.)
/// </summary>
/// <typeparam name="TRequest">The request/message type</typeparam>
/// <typeparam name="TResponse">The response type</typeparam>
/// <docs>extensibility/hooks-and-middleware</docs>
public interface IPipelineBehavior<in TRequest, TResponse> {
  /// <summary>
  /// Handle the request by executing behavior logic and optionally invoking the next behavior or handler.
  /// </summary>
  /// <param name="request">The message being dispatched</param>
  /// <param name="next">Delegate to invoke the next behavior or handler in the pipeline</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The response (potentially modified by this behavior)</returns>
  /// <example>
  /// <code>
  /// public async Task&lt;TResponse&gt; Handle(
  ///     TRequest request,
  ///     Func&lt;Task&lt;TResponse&gt;&gt; next,
  ///     CancellationToken cancellationToken
  /// ) {
  ///     // Before: Pre-process request
  ///     _logger.LogInformation("Processing {Request}", typeof(TRequest).Name);
  ///
  ///     // Execute next behavior or handler
  ///     var response = await next();
  ///
  ///     // After: Post-process response
  ///     _logger.LogInformation("Completed {Request}", typeof(TRequest).Name);
  ///
  ///     return response;
  /// }
  /// </code>
  /// </example>
  Task<TResponse> Handle(
    TRequest request,
    Func<Task<TResponse>> next,
    CancellationToken cancellationToken = default
  );
}

/// <summary>
/// Base class for pipeline behaviors that provides common functionality.
/// </summary>
/// <typeparam name="TRequest">The request/message type</typeparam>
/// <typeparam name="TResponse">The response type</typeparam>
public abstract class PipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> {
  /// <summary>
  /// Handle the request. Override this to implement custom behavior logic.
  /// </summary>
  public abstract Task<TResponse> Handle(
    TRequest request,
    Func<Task<TResponse>> next,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Executes the next behavior or handler in the pipeline.
  /// Use this helper method to clearly separate pre/post processing logic.
  /// </summary>
  protected async Task<TResponse> ExecuteNextAsync(Func<Task<TResponse>> next) {
    return await next();
  }
}
