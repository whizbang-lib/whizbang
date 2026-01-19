using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

public class WhizbangIdRegistrationGenerationTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GeneratesRegistrationClassAsync() {
    // Arrange
    var source = @"
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
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
    await Assert.That(registrationSource!).Contains("[System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(registrationSource).Contains("public static void Initialize()");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistrationCallsRegisterFactoryAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
    await Assert.That(registrationSource!).Contains("WhizbangIdProviderRegistry.RegisterFactory<");
    await Assert.That(registrationSource).Contains("new TestIdProvider(baseProvider)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistrationHasRegisterAllMethodAsync() {
    // Arrange
    var source = @"
      using Whizbang.Core;

      [WhizbangId]
      public readonly partial struct TestId;
    ";

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert
    var registrationSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdProviderRegistration.g.cs");
    await Assert.That(registrationSource).IsNotNull();
    await Assert.That(registrationSource!).Contains("public static void RegisterAll(");
    await Assert.That(registrationSource).Contains("IServiceCollection services");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistrationRegistersAllProvidersAsync() {
    // Arrange
    var source = @"
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
    await Assert.That(registrationSource!).Contains("AddSingleton<global::Whizbang.Core.IWhizbangIdProvider<");

    // Count should be 2 (one for each ID type)
    var addSingletonCount = registrationSource.Split("AddSingleton<global::Whizbang.Core.IWhizbangIdProvider<").Length - 1;
    await Assert.That(addSingletonCount).IsEqualTo(2);
  }
}
