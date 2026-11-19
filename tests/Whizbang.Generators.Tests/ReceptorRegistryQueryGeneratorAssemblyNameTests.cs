// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The receptor registry contribution names the assembly that declared the receptors, so a host's query counts only
/// its own service's (#2243); a compilation with no name has none to give.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ReceptorRegistryQueryGenerator.cs</code-under-test>
public class ReceptorRegistryQueryGeneratorAssemblyNameTests {
  private const string FILE = "WhizbangReceptorRegistryQueryRegistration.g.cs";

  private const string SOURCE = """
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;

    namespace Orders;

    public record OrderPlaced : IEvent;

    public class OrderPlacedReceptor : IReceptor<OrderPlaced> {
      public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    """;

  [Test]
  public async Task TheContribution_NamesItsAssemblyAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorRegistryQueryGenerator>(SOURCE);

    await Assert.That(GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE)).Contains("AssemblyName = \"TestAssembly\",");
  }

  [Test]
  public async Task AnUnnamedCompilation_EmitsNoAssemblyNameAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new ReceptorRegistryQueryGenerator(), SOURCE);

    await Assert.That(GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE)).DoesNotContain("AssemblyName =");
  }
}
