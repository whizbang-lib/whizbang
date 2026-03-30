using Whizbang.Core;

namespace Whizbang.Transports.Mutations;

/// <summary>
/// Marks a command class for automatic endpoint generation.
/// Source generators discover this attribute and generate transport-specific endpoints:
/// - REST endpoints via FastEndpoints
/// - GraphQL mutations via HotChocolate
/// Each transport package implements its own generator.
/// </summary>
/// <typeparam name="TCommand">The command type that must implement <see cref="ICommand"/>.</typeparam>
/// <typeparam name="TResult">The result type returned after command execution.</typeparam>
/// <docs>apis/mutations/command-endpoints</docs>
/// <tests>tests/Whizbang.Transports.Mutations.Tests/Unit/CommandEndpointAttributeTests.cs</tests>
/// <example>
/// // Simple - command is the request
/// [CommandEndpoint&lt;CreateOrderCommand, OrderResult&gt;(
///     RestRoute = "/api/orders",
///     GraphQLMutation = "createOrder")]
/// public class CreateOrderCommand : ICommand {
///     public required string CustomerId { get; init; }
/// }
///
/// // With custom request DTO
/// [CommandEndpoint&lt;CreateOrderCommand, OrderResult&gt;(
///     RestRoute = "/api/orders",
///     GraphQLMutation = "createOrder",
///     RequestType = typeof(CreateOrderRequest))]
/// public class CreateOrderCommand : ICommand { }
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
#pragma warning disable S2326 // Type parameters carry type info for source generators — used at compile time, not runtime
public sealed class CommandEndpointAttribute<TCommand, TResult> : Attribute
    where TCommand : ICommand {
#pragma warning restore S2326
  /// <summary>
  /// The REST route for the FastEndpoints endpoint.
  /// If null, no REST endpoint is generated.
  /// Example: "/api/orders" or "/api/v1/orders/{id}"
  /// </summary>
  /// <docs>apis/mutations/command-endpoints#rest-route</docs>
  public string? RestRoute { get; set; }

  /// <summary>
  /// The GraphQL mutation field name.
  /// If null, no GraphQL mutation is generated.
  /// Example: "createOrder" generates mutation { createOrder(...) { ... } }
  /// </summary>
  /// <docs>apis/mutations/command-endpoints#graphql-mutation</docs>
  public string? GraphQLMutation { get; set; }

  /// <summary>
  /// Optional custom request DTO type.
  /// When specified, the generated endpoint accepts this type as input
  /// instead of the command directly. User must implement
  /// <c>MapRequestToCommandAsync</c> in their partial class.
  /// </summary>
  /// <docs>apis/mutations/custom-request-dto</docs>
  public Type? RequestType { get; set; }
}
