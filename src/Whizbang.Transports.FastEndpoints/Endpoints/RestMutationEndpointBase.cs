using Whizbang.Core;
using Whizbang.Transports.Mutations;

namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Base class for REST mutation endpoints using FastEndpoints.
/// Inherits from <see cref="MutationEndpointBase{TCommand, TResult}"/> to provide
/// consistent hook behavior (OnBefore, OnAfter, OnError) across transports.
/// Generated endpoint classes are partial, allowing users to override hooks.
/// </summary>
/// <typeparam name="TCommand">The command type that implements <see cref="ICommand"/>.</typeparam>
/// <typeparam name="TResult">The result type returned after command execution.</typeparam>
/// <docs>v0.1.0/rest/mutations</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/RestMutationEndpointBaseTests.cs</tests>
/// <example>
/// // Generated endpoint (partial, user can extend):
/// [CommandEndpoint&lt;CreateOrderCommand, OrderResult&gt;(RestRoute = "/api/orders")]
/// public partial class CreateOrderEndpoint : RestMutationEndpointBase&lt;CreateOrderCommand, OrderResult&gt; {
///     public override void Configure() {
///         Post("/api/orders");
///     }
///
///     public override async Task HandleAsync(CreateOrderCommand cmd, CancellationToken ct) {
///         var result = await ExecuteAsync(cmd, ct);
///         await SendAsync(result, cancellation: ct);
///     }
/// }
///
/// // User's partial class for customization:
/// public partial class CreateOrderEndpoint {
///     protected override async ValueTask OnBeforeExecuteAsync(
///         CreateOrderCommand command,
///         IMutationContext context,
///         CancellationToken ct) {
///         await _validator.ValidateAndThrowAsync(command, ct);
///     }
/// }
/// </example>
public abstract class RestMutationEndpointBase<TCommand, TResult>
    : MutationEndpointBase<TCommand, TResult>
    where TCommand : ICommand {
}
