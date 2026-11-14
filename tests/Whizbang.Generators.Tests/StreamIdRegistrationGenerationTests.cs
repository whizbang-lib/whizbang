// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated registrations name each model's Guid <c>[StreamId]</c> property, so a filter on it can also
/// be answered by the row's primary key (#2302). A key the row's Guid id cannot be compared with is left out.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
/// <docs>fundamentals/perspectives/physical-fields#stream-id-filters</docs>
[Category("Unit")]
public class StreamIdRegistrationGenerationTests {
  private const string REGISTER = "Whizbang.Data.EFCore.Postgres.QueryTranslation.StreamIdRegistry.Register<";

  private static string _source(string streamIdDeclaration) => $$"""
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record OrderDto {
      {{streamIdDeclaration}}
      public string Name { get; init; } = string.Empty;
    }

    public class OrderPerspective(IPerspectiveStore<OrderDto> store)
      : IPerspectiveFor<OrderDto, OrderCreated> {
      public OrderDto Apply(OrderDto currentData, OrderCreated @event) => currentData;
    }

    public record OrderCreated : IEvent;

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedAsync(string streamIdDeclaration) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(streamIdDeclaration));
    return string.Concat(result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  [Test]
  public async Task GuidStreamId_IsRegisteredAsync() {
    var code = await _generatedAsync("[StreamId] public Guid OrderId { get; init; }");

    await Assert.That(code).Contains(REGISTER + "global::TestApp.OrderDto>(\"OrderId\");");
  }

  [Test]
  public async Task NullableGuidStreamId_IsRegisteredAsync() {
    var code = await _generatedAsync("[StreamId] public Guid? OrderId { get; init; }");

    await Assert.That(code).Contains(REGISTER + "global::TestApp.OrderDto>(\"OrderId\");");
  }

  [Test]
  public async Task StringStreamId_IsNotRegisteredAsync() {
    var code = await _generatedAsync("[StreamId] public string OrderId { get; init; } = string.Empty;");

    await Assert.That(code).DoesNotContain(REGISTER)
      .Because("the row's id is a Guid, so the database has no primary-key comparison for a string key");
  }

  [Test]
  public async Task NoStreamId_IsNotRegisteredAsync() {
    var code = await _generatedAsync("public Guid OrderId { get; init; }");

    await Assert.That(code).DoesNotContain(REGISTER);
  }
}
