using Whizbang.Core;
using Whizbang.Transports.Mutations;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Base class for GraphQL mutations using HotChocolate.
/// Inherits from <see cref="MutationEndpointBase{TCommand, TResult}"/> to provide
/// consistent hook behavior (OnBefore, OnAfter, OnError) across transports.
/// Generated mutation classes are partial, allowing users to override hooks.
/// </summary>
/// <typeparam name="TCommand">The command type that implements <see cref="ICommand"/>.</typeparam>
/// <typeparam name="TResult">The result type returned after command execution.</typeparam>
/// <docs>v0.1.0/graphql/mutations</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/GraphQLMutationBaseTests.cs</tests>
/// <example>
/// // Generated mutation type (partial, user can extend):
/// [ExtendObjectType(OperationTypeNames.Mutation)]
/// public partial class CreateOrderMutation : GraphQLMutationBase&lt;CreateOrderCommand, OrderResult&gt; {
///     public async Task&lt;OrderResult&gt; CreateOrderAsync(
///         CreateOrderCommand command,
///         CancellationToken ct) =&gt; await ExecuteAsync(command, ct);
/// }
///
/// // User's partial class for customization:
/// public partial class CreateOrderMutation {
///     protected override async ValueTask OnBeforeExecuteAsync(
///         CreateOrderCommand command,
///         IMutationContext context,
///         CancellationToken ct) {
///         await _validator.ValidateAndThrowAsync(command, ct);
///     }
/// }
/// </example>
public abstract class GraphQLMutationBase<TCommand, TResult>
    : MutationEndpointBase<TCommand, TResult>
    where TCommand : ICommand {
}
