// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

public class WhizbangIdRegistrationGenerationTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesRegistrationClassAsync() {
    // Arrange
    const string source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistrationHasModuleInitializerAsync() {
    // Arrange
    const string source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
    await Assert.That(registrationSource).Contains("[System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(registrationSource).Contains("public static void Initialize()");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistrationCallsRegisterFactoryAsync() {
    // Arrange
    const string source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
    await Assert.That(registrationSource).Contains("WhizbangIdProviderRegistry.RegisterFactory<");
    await Assert.That(registrationSource).Contains("new global::TestIdProvider(baseProvider)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistrationRegistersEachIdsGuidConversionAsync() {
    // Arrange
    const string source = @"
      using Whizbang.Core;

      namespace MyApp.Ids {
        [WhizbangId]
        public readonly partial struct OrderId;

        [WhizbangId(AcceptAnyVersion = true)]
        public readonly partial struct ExternalUserId;
      }
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
    await Assert.That(registrationSource).Contains(
      "global::Whizbang.Core.WhizbangIdProviderRegistry.RegisterGuidConversion<global::MyApp.Ids.OrderId>(global::MyApp.Ids.OrderId.From);");
    await Assert.That(registrationSource).Contains(
      "global::Whizbang.Core.WhizbangIdProviderRegistry.RegisterGuidConversion<global::MyApp.Ids.ExternalUserId>(global::MyApp.Ids.ExternalUserId.From);");
    await Assert.That(registrationSource).Contains(
      "global::Whizbang.Core.WhizbangIdProviderRegistry.RegisterValueMember<global::MyApp.Ids.OrderId>(static id => id.Value);");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistrationHasRegisterAllMethodAsync() {
    // Arrange
    const string source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
    await Assert.That(registrationSource).Contains("public static void RegisterAll(");
    await Assert.That(registrationSource).Contains("IServiceCollection services");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistrationRegistersAllProvidersAsync() {
    // Arrange
    const string source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId1;

      [WhizbangId]
      public readonly partial struct TestId2;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
    await Assert.That(registrationSource).Contains("AddSingleton<global::Whizbang.Core.IWhizbangIdProvider<");

    // Count should be 2 (one for each ID type)
    var addSingletonCount = registrationSource.Split("AddSingleton<global::Whizbang.Core.IWhizbangIdProvider<").Length - 1;
    await Assert.That(addSingletonCount).IsEqualTo(2);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IdsInSeveralNamespaces_RegistrationCompilesAsync() {
    // The registration is emitted once, under the first id's namespace, so it names every id and provider in full
    // (#2072). A name relative to that namespace does not resolve for an id declared in another one (CS0246).
    const string source = """
      using Whizbang.Core;

      namespace App.Orders {
        [WhizbangId]
        public readonly partial struct OrderId;
      }

      namespace App.Billing {
        [WhizbangId]
        public readonly partial struct InvoiceId;
      }

      [WhizbangId]
      public readonly partial struct GlobalId;
      """;

    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<WhizbangIdGenerator>(source);

    await Assert.That(errors).IsEmpty()
      .Because("each provider lives in its own id's namespace, which the registration's namespace does not import");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IdsInSeveralNamespaces_RegistrationNamesEachProviderInFullAsync() {
    const string source = """
      using Whizbang.Core;

      namespace App.Orders {
        [WhizbangId]
        public readonly partial struct OrderId;
      }

      [WhizbangId]
      public readonly partial struct GlobalId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    var registrationSource = GeneratorTestHelper.GetRequiredGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).Contains(
      "RegisterFactory<global::App.Orders.OrderId>(baseProvider => new global::App.Orders.OrderIdProvider(baseProvider));");
    await Assert.That(registrationSource).Contains(
      "RegisterFactory<global::GlobalId>(baseProvider => new global::GlobalIdProvider(baseProvider));");
  }
}
