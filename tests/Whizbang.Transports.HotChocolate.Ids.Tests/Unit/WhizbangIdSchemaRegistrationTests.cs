// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using ConsumerEventId = Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures.Consumer.EventId;
using ConsumerStreamId = Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures.Consumer.StreamId;
using FrameworkMessageId = Whizbang.Core.ValueObjects.MessageId;
using FrameworkStreamId = Whizbang.Core.ValueObjects.StreamId;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Unit;

#pragma warning disable S2325, CA1822 // HotChocolate binds the instance methods of a query type as its resolvers
/// <summary>Resolvers that take consumer ids named like the framework's own ids.</summary>
public class FrameworkNamedIdQuery {
  public string Stream(ConsumerStreamId id) => id.Value.ToString();
  public string Event(ConsumerEventId id) => id.Value.ToString();
}

/// <summary>A resolver that takes one of the framework's own ids.</summary>
public class FrameworkIdQuery {
  public string Message(FrameworkMessageId id) => id.Value.ToString();
}

/// <summary>A resolver that returns a type implementing IWhizbangId by hand, not generated from [WhizbangId].</summary>
public class HandWrittenIdQuery {
  public HandWrittenId HandWritten() => new(Guid.CreateVersion7());
}
#pragma warning restore S2325, CA1822

/// <summary>An <see cref="IWhizbangId"/> written by hand: the registry has no conversion for it.</summary>
#pragma warning disable CA1036 // a fixture the schema only names; nothing compares it
public readonly record struct HandWrittenId(Guid Value) : IWhizbangId {
  public Guid ToGuid() => Value;
  public bool IsTimeOrdered => true;
  public bool SubMillisecondPrecision => false;
  public DateTimeOffset Timestamp => DateTimeOffset.UnixEpoch;
  public bool Equals(IWhizbangId? other) => other is not null && other.ToGuid() == Value;
  public int CompareTo(IWhizbangId? other) => other is null ? 1 : Value.CompareTo(other.ToGuid());
}
#pragma warning restore CA1036

/// <summary>
/// Which id scalars a schema gets: on by default with <c>AddWhizbangLenses</c>, safe to add twice, and only for the
/// ids the schema takes or returns, so the framework's own ids add nothing unless used (#2073).
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdSchemaRegistrationTests {
  private const string V4 = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

  [Test]
  public async Task AddWhizbangLenses_AddsTheIdScalarsAsync() {
    var executor = await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<StrongIdQuery>()
      .AddWhizbangLenses()
      .BuildRequestExecutorAsync();
    var sdl = executor.Schema.ToString();

    await Assert.That(sdl).Contains("order(id: GraphQLOrderId!): String!");
    await Assert.That(sdl).Contains("scalar GraphQLOrderId");
    await Assert.That(sdl).DoesNotContain("input GraphQLOrderIdInput");
  }

  [Test]
  public async Task AddWhizbangIds_WithAddWhizbangLensesAndTwice_AddsEachScalarOnceAsync() {
    var guid = Guid.CreateVersion7();
    var executor = await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<StrongIdQuery>()
      .AddWhizbangLenses()
      .AddWhizbangIds()
      .AddWhizbangIds()
      .BuildRequestExecutorAsync();
    var sdl = executor.Schema.ToString();

    var result = (IOperationResult)await executor.ExecuteAsync($"{{ order(id: \"{guid}\") }}");

    await Assert.That(sdl.Split("scalar GraphQLOrderId").Length).IsEqualTo(2);
    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"order\": \"{guid}\"");
  }

  [Test]
  public async Task AddWhizbangIds_OnTwoNamedSchemas_AddsTheScalarsToEachAsync() {
    var services = new ServiceCollection();
    services.AddGraphQL("first").AddQueryType<StrongIdQuery>().AddWhizbangIds();
    services.AddGraphQL("second").AddQueryType<StrongIdQuery>().AddWhizbangIds();
    var provider = services.BuildServiceProvider();
    var resolver = provider.GetRequiredService<IRequestExecutorResolver>();

    var first = await resolver.GetRequestExecutorAsync("first");
    var second = await resolver.GetRequestExecutorAsync("second");

    await Assert.That(first.Schema.ToString()).Contains("scalar GraphQLOrderId");
    await Assert.That(second.Schema.ToString()).Contains("scalar GraphQLOrderId");
  }

  [Test]
  public async Task HandWrittenId_WithNoGeneratedConversion_GetsNoScalarAsync() {
    var executor = await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<HandWrittenIdQuery>()
      .AddWhizbangIds()
      .BuildRequestExecutorAsync();
    var sdl = executor.Schema.ToString();

    await Assert.That(sdl).Contains("handWritten: HandWrittenId!");
    await Assert.That(sdl).DoesNotContain("scalar HandWrittenId");
  }

  [Test]
  public async Task ConsumerIdsNamedLikeFrameworkIds_GetTheConsumersScalarsAndNoFrameworkScalarAsync() {
    // Whizbang.Core's own ids are in the registry, as they are in any application.
    _ = FrameworkStreamId.New();
    await Assert.That(WhizbangIdProviderRegistry.GetGuidConversions().ContainsKey(typeof(FrameworkStreamId))).IsTrue();

    var executor = await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<FrameworkNamedIdQuery>()
      .AddWhizbangIds()
      .BuildRequestExecutorAsync();
    var sdl = executor.Schema.ToString();

    await Assert.That(sdl).Contains("stream(id: StreamId!): String!");
    await Assert.That(sdl).Contains("event(id: EventId!): String!");
    await Assert.That(sdl).Contains("scalar StreamId");
    await Assert.That(sdl).Contains("scalar EventId");
    await Assert.That(sdl).DoesNotContain("scalar MessageId");
    await Assert.That(sdl).DoesNotContain("scalar CorrelationId");

    // The framework's ids take UUIDv7 only; the consumer's take any version, so a UUIDv4 shows whose scalar it is.
    var result = (IOperationResult)await executor.ExecuteAsync($"{{ stream(id: \"{V4}\") event(id: \"{V4}\") }}");

    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"stream\": \"{V4}\"");
    await Assert.That(result.ToJson()).Contains($"\"event\": \"{V4}\"");
  }

  [Test]
  public async Task FrameworkId_TheSchemaTakes_GetsItsScalarAsync() {
    var guid = Guid.CreateVersion7();
    var executor = await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<FrameworkIdQuery>()
      .AddWhizbangIds()
      .BuildRequestExecutorAsync();
    var sdl = executor.Schema.ToString();

    var result = (IOperationResult)await executor.ExecuteAsync($"{{ message(id: \"{guid}\") }}");

    await Assert.That(sdl).Contains("message(id: MessageId!): String!");
    await Assert.That(sdl).Contains("scalar MessageId");
    await Assert.That(sdl).DoesNotContain("scalar StreamId");
    await Assert.That(result.Errors).IsNull();
    await Assert.That(result.ToJson()).Contains($"\"message\": \"{guid}\"");
  }
}
