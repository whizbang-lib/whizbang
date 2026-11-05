// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.HotChocolate.Tests.Fixtures;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>An input type with a strong id field.</summary>
public sealed class OrderLookupInput {
  public GraphQLOrderId OrderId { get; init; }
}

#pragma warning disable S2325, CA1822 // HotChocolate binds the instance methods of a query type as its resolvers
/// <summary>Resolvers that take and return strong ids.</summary>
public class StrongIdQuery {
  public string Order(GraphQLOrderId id) => id.Value.ToString();
  public string OrderFromInput(OrderLookupInput input) => input.OrderId.Value.ToString();
  public string OptionalOrder(GraphQLOrderId? id) => id?.Value.ToString() ?? "none";
  public int OrderCount(IReadOnlyList<GraphQLOrderId> ids) => ids.Count;
  public string User(GraphQLExternalUserId id) => id.Value.ToString();
  public GraphQLOrderId EchoOrder(GraphQLOrderId id) => id;
}
#pragma warning restore S2325, CA1822

/// <summary>
/// A <c>[WhizbangId]</c> type as a GraphQL argument, input field and result, through <c>AddWhizbangIds</c> (#2073).
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdGraphQLTests {
  private const string V4 = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
  private const string EMPTY = "00000000-0000-0000-0000-000000000000";

  private static async Task<IRequestExecutor> _executorAsync() =>
    await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<StrongIdQuery>()
      .AddWhizbangIds()
      .BuildRequestExecutorAsync();

  private static async Task<IOperationResult> _executeAsync(string document, Dictionary<string, object?>? variables = null) {
    var executor = await _executorAsync();
    var request = OperationRequestBuilder.New().SetDocument(document);
    if (variables is not null) {
      request.SetVariableValues(variables);
    }

    return (IOperationResult)await executor.ExecuteAsync(request.Build());
  }

  [Test]
  public async Task Schema_ShowsEachIdAsANamedUuidScalarAsync() {
    var executor = await _executorAsync();
    var sdl = executor.Schema.ToString();

    await Assert.That(sdl).Contains("order(id: GraphQLOrderId!): String!");
    await Assert.That(sdl).Contains("orderId: GraphQLOrderId!");
    await Assert.That(sdl).Contains("user(id: GraphQLExternalUserId!): String!");
    await Assert.That(sdl).Contains("scalar GraphQLOrderId @specifiedBy(url: \"https:\\/\\/www.rfc-editor.org\\/rfc\\/rfc9562\")");
    await Assert.That(sdl).DoesNotContain("input GraphQLOrderIdInput");
  }

  [Test]
  public async Task Argument_WithAUuidV7_ReachesTheResolverAsTheIdAsync() {
    var guid = Guid.CreateVersion7();

    var result = await _executeAsync($"{{ order(id: \"{guid}\") }}");

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"order\": \"{guid}\"");
  }

  [Test]
  public async Task Argument_FromAVariable_ReachesTheResolverAsTheIdAsync() {
    var guid = Guid.CreateVersion7();

    var result = await _executeAsync(
      "query($id: GraphQLOrderId!) { order(id: $id) }",
      new Dictionary<string, object?> { ["id"] = guid.ToString() });

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"order\": \"{guid}\"");
  }

  [Test]
  public async Task InputField_WithAUuidV7_ReachesTheResolverAsTheIdAsync() {
    var guid = Guid.CreateVersion7();

    var result = await _executeAsync($"{{ orderFromInput(input: {{ orderId: \"{guid}\" }}) }}");

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"orderFromInput\": \"{guid}\"");
  }

  [Test]
  public async Task OptionalArgument_LeftOut_ReachesTheResolverAsNullAsync() {
    var result = await _executeAsync("{ optionalOrder }");

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains("\"optionalOrder\": \"none\"");
  }

  [Test]
  public async Task ListArgument_ReachesTheResolverAsIdsAsync() {
    var result = await _executeAsync($"{{ orderCount(ids: [\"{Guid.CreateVersion7()}\", \"{Guid.CreateVersion7()}\"]) }}");

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains("\"orderCount\": 2");
  }

  [Test]
  public async Task Result_OfAnIdType_IsWrittenAsAUuidStringAsync() {
    var guid = Guid.CreateVersion7();

    var result = await _executeAsync($"{{ echoOrder(id: \"{guid}\") }}");

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"echoOrder\": \"{guid}\"");
  }

  [Test]
  public async Task Argument_WithANonV7Guid_IsAGraphQLErrorAsync() {
    var result = await _executeAsync($"{{ order(id: \"{V4}\") }}");

    await _assertInvalidIdAsync(result, "GraphQLOrderId requires UUIDv7");
  }

  [Test]
  public async Task Argument_FromAVariableWithANonV7Guid_IsAGraphQLErrorAsync() {
    var result = await _executeAsync(
      "query($id: GraphQLOrderId!) { order(id: $id) }",
      new Dictionary<string, object?> { ["id"] = V4 });

    await _assertInvalidIdAsync(result, "GraphQLOrderId requires UUIDv7");
  }

  [Test]
  public async Task InputField_WithANonV7Guid_IsAGraphQLErrorAsync() {
    var result = await _executeAsync($"{{ orderFromInput(input: {{ orderId: \"{V4}\" }}) }}");

    await _assertInvalidIdAsync(result, "GraphQLOrderId requires UUIDv7");
  }

  [Test]
  public async Task Argument_ThatIsNotAUuid_IsAGraphQLErrorAsync() {
    var result = await _executeAsync("{ order(id: \"not-a-uuid\") }");

    await _assertInvalidIdAsync(result, "it is not a UUID");
  }

  [Test]
  public async Task AnyVersionId_WithANonV7Guid_ReachesTheResolverAsync() {
    var result = await _executeAsync($"{{ user(id: \"{V4}\") }}");

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"user\": \"{V4}\"");
  }

  [Test]
  public async Task AnyVersionId_WithTheEmptyGuid_IsAGraphQLErrorAsync() {
    var result = await _executeAsync($"{{ user(id: \"{EMPTY}\") }}");

    await _assertInvalidIdAsync(result, "GraphQLExternalUserId requires a non-empty Guid");
  }

  [Test]
  public async Task DefaultId_WithTheEmptyGuid_IsAGraphQLErrorAsync() {
    var result = await _executeAsync($"{{ order(id: \"{EMPTY}\") }}");

    await _assertInvalidIdAsync(result, "GraphQLOrderId requires UUIDv7");
  }

  private static async Task _assertInvalidIdAsync(IOperationResult result, string reason) {
    await Assert.That(result.Data).IsNull();
    await Assert.That(result.Errors).IsNotNull();
    await Assert.That(result.Errors!.Count).IsEqualTo(1);
    var json = result.ToJson();
    await Assert.That(json).Contains(reason);
    await Assert.That(json).Contains("WHIZBANG_INVALID_ID");
    await Assert.That(json).DoesNotContain("Unexpected Execution Error");
  }
}
