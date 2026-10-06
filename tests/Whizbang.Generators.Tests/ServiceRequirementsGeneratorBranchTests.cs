// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The container constructs a registered type through a public constructor, so a type's
/// requirements are read from the public one even when a non-public constructor asks for more.
/// </summary>
/// <tests>src/Whizbang.Generators/ServiceRequirementsGenerator.cs</tests>
[Category("SourceGenerators")]
public class ServiceRequirementsGeneratorBranchTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonPublicConstructor_IsNotTheOneTheContainerUsesAsync() {
    var result = GeneratorTestHelper.RunGenerator<ServiceRequirementsGenerator>("""
      using Microsoft.Extensions.DependencyInjection;
      namespace TestApp;
      public interface IClock { }
      public interface IStore { }
      public sealed class Worker {
        public Worker(IClock clock) { }
        internal Worker(IClock clock, IStore store) { }
      }
      public static class Registration {
        public static IServiceCollection AddThing(this IServiceCollection services) {
          services.AddSingleton<Worker>();
          return services;
        }
      }
      """);
    var generated = string.Concat(GeneratorTestHelper.GetAllGeneratedSources(result).Select(s => s.Source));

    await Assert.That(generated).Contains("IClock");
    await Assert.That(generated).DoesNotContain("IStore");
  }
}
