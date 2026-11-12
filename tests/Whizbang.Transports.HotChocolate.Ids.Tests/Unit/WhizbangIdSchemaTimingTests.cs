// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Transports.HotChocolate.Ids.Fixtures;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Unit;

#pragma warning disable S2325, CA1822 // HotChocolate binds the instance methods of a query type as its resolvers
/// <summary>A resolver that takes an id from an assembly no code has run in yet.</summary>
public class UntouchedIdQuery {
  public string Untouched(UntouchedOrderId id) => id.Value.ToString();
}
#pragma warning restore S2325, CA1822

/// <summary>
/// A module initializer runs when its assembly is first used, not when it is referenced. The schema still gets the
/// scalar of an id whose assembly nothing has used before the schema is built (#2073).
/// </summary>
[Category("Unit")]
[Category("WhizbangId")]
public class WhizbangIdSchemaTimingTests {
  private const string V4 = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
  private const string UNTOUCHED_ID_NAME = "UntouchedOrderId";

  [Test]
  public async Task Id_FromAnAssemblyNothingHasUsed_GetsItsScalarWhenTheSchemaIsBuiltAsync() {
    // Nothing has loaded the id's type yet, so its module initializer has not recorded it. The check goes by name:
    // loading the type (typeof) is itself enough to run the initializer on CoreCLR.
    await Assert.That(WhizbangIdProviderRegistry.GetGuidConversions().Keys.Any(type => type.Name == UNTOUCHED_ID_NAME))
      .IsFalse();

    var executor = await new ServiceCollection()
      .AddGraphQL()
      .AddQueryType<UntouchedIdQuery>()
      .AddWhizbangIds()
      .BuildRequestExecutorAsync();
    var sdl = executor.Schema.ToString();

    await Assert.That(sdl).Contains("untouched(id: UntouchedOrderId!): String!");
    await Assert.That(sdl).Contains("scalar UntouchedOrderId");
    await Assert.That(sdl).DoesNotContain("input UntouchedOrderIdInput");

    var result = (IOperationResult)await executor.ExecuteAsync($"{{ untouched(id: \"{V4}\") }}");

    await Assert.That(result.ToJson()).Contains("UntouchedOrderId requires UUIDv7");
    await Assert.That(result.ToJson()).Contains("WHIZBANG_INVALID_ID");
  }
}
