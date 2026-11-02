// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #1725: the generator registers, by name, every concrete type kept full by <c>[StoredMetadata(StoredMetadataForm.Full)]</c>
/// on itself, a base class or an interface, with the nearest declaration winning, so the framework reads no attribute by
/// reflection.
/// </summary>
/// <docs>data/event-store#keeping-an-event-type-full</docs>
/// <code-under-test>src/Whizbang.Generators/StoredMetadataRegistryGenerator.cs</code-under-test>
public class StoredMetadataRegistryGeneratorTests {

  private const string SOURCE = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;

    namespace MyApp;

    [StoredMetadata(StoredMetadataForm.Full)]
    public record PaymentCaptured : IEvent;

    [StoredMetadata(StoredMetadataForm.Full)]
    public abstract record FullBase : IEvent;

    public record DerivedFromFull : FullBase;

    [StoredMetadata(StoredMetadataForm.Default)]
    public record OptedBack : FullBase;

    [StoredMetadata(StoredMetadataForm.Full)]
    public interface IKeptFull : IEvent;

    public record ThroughInterface : IKeptFull;

    public record Unattributed : IEvent;

    public static class Holder {
      [StoredMetadata(StoredMetadataForm.Full)]
      private sealed record Hidden : IEvent;
    }

    [StoredMetadata(StoredMetadataForm.Full)]
    public record Open<T> : IEvent;
    """;

  private static string _generated() {
    var result = GeneratorTestHelper.RunGenerator<StoredMetadataRegistryGenerator>(SOURCE);
    return GeneratorTestHelper.GetGeneratedSource(result, "StoredMetadataRegistry.g.cs")!;
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistersEachFullType_ByItsClrNameAsync() {
    var generated = _generated();

    await Assert.That(generated).Contains("global::Whizbang.Core.Messaging.StoredMetadataRegistry.RegisterFull(\"MyApp.PaymentCaptured\");");
    await Assert.That(generated).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(generated).DoesNotContain("Unattributed").Because("a type with no attribute in its chain stays at the write form");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ABaseOrInterfaceDeclaration_KeepsTheDerivedTypeFullAsync() {
    var generated = _generated();

    await Assert.That(generated).Contains("RegisterFull(\"MyApp.DerivedFromFull\")").Because("a base class's declaration is inherited");
    await Assert.That(generated).Contains("RegisterFull(\"MyApp.ThroughInterface\")").Because("so is an interface's");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_TheNearestDeclarationWins_AndAbstractOrInterfaceTypesAreNotListedAsync() {
    var generated = _generated();

    await Assert.That(generated).DoesNotContain("\"MyApp.OptedBack\"").Because("its own Default is nearer than its base's Full");
    await Assert.That(generated).DoesNotContain("\"MyApp.FullBase\"").Because("an abstract type is never stored");
    await Assert.That(generated).DoesNotContain("\"MyApp.IKeptFull\"").Because("an interface is never stored");
    await Assert.That(generated).DoesNotContain("Open").Because("an open generic has no stored name");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_APrivateNestedType_IsRegisteredByNameAsync() {
    var generated = _generated();

    await Assert.That(generated).Contains("RegisterFull(\"MyApp.Holder+Hidden\")")
      .Because("the registry takes names, so a type generated code cannot name is kept full like any other");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoFullTypes_GeneratesNothingAsync() {
    var result = GeneratorTestHelper.RunGenerator<StoredMetadataRegistryGenerator>("""
      using Whizbang.Core;
      namespace MyApp;
      public record Plain : IEvent;
      """);

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "StoredMetadataRegistry.g.cs")).IsNull();
  }
}
