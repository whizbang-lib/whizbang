// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #1580: the generator registers every event type that carries [AuditEvent], directly or through a base class,
/// with the attribute's values, so audit decisions read no attribute by reflection.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/AuditEventRegistryGenerator.cs</code-under-test>
public class AuditEventRegistryGeneratorTests {

  private const string SOURCE = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Audit;

    namespace MyApp;

    [AuditEvent(Reason = "money moved", Level = AuditLevel.Critical)]
    public record PaymentTaken : IEvent;

    [AuditEvent(Exclude = true)]
    public abstract record NoisyBase : IEvent;

    public record PresencePinged : NoisyBase;

    public record Unattributed : IEvent;

    [AuditEvent]
    public record BareAudited : IEvent;

    [AuditEvent(Exclude = false, Tag = "kept")]
    public record ExplicitlyKept : IEvent;

    public static class Holder {
      [AuditEvent(Exclude = true)]
      private sealed record Hidden : IEvent;
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistersEachAttributedTypeWithItsValuesAsync() {
    var result = GeneratorTestHelper.RunGenerator<AuditEventRegistryGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "AuditEventRegistry.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains(
      "global::Whizbang.Core.SystemEvents.AuditEventRegistry.Register(typeof(global::MyApp.PaymentTaken), new global::Whizbang.Core.Attributes.AuditEventAttribute { Reason = \"money moved\", Level = (global::Whizbang.Core.Audit.AuditLevel)");
    await Assert.That(generated).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(generated).DoesNotContain("Unattributed")
      .Because("a type with no [AuditEvent] anywhere in its chain has nothing to register");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ADerivedType_IsRegisteredWithItsBasesAttributeAsync() {
    var result = GeneratorTestHelper.RunGenerator<AuditEventRegistryGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "AuditEventRegistry.g.cs");

    await Assert.That(generated).Contains(
      "Register(typeof(global::MyApp.PresencePinged), new global::Whizbang.Core.Attributes.AuditEventAttribute { Exclude = true })")
      .Because("[AuditEvent] is Inherited = true, and reflection used to find it on the base");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AnUnreachableType_IsReportedNotRegisteredAsync() {
    var result = GeneratorTestHelper.RunGenerator<AuditEventRegistryGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "AuditEventRegistry.g.cs");

    await Assert.That(generated).DoesNotContain("Hidden");
    var warning = result.Diagnostics.SingleOrDefault(d => d.Id == "WHIZ180");
    await Assert.That(warning).IsNotNull()
      .Because("a private type cannot be named from generated code, so its audit settings would be lost without a word");
    await Assert.That(warning!.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Hidden");
    await Assert.That(warning.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ABareAttributeAndAnExplicitFalse_AreRegisteredAsWrittenAsync() {
    var result = GeneratorTestHelper.RunGenerator<AuditEventRegistryGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "AuditEventRegistry.g.cs");

    await Assert.That(generated).Contains(
      "Register(typeof(global::MyApp.BareAudited), new global::Whizbang.Core.Attributes.AuditEventAttribute());")
      .Because("an attribute with no arguments registers the defaults");
    await Assert.That(generated).Contains(
      "Register(typeof(global::MyApp.ExplicitlyKept), new global::Whizbang.Core.Attributes.AuditEventAttribute { Exclude = false, Tag = \"kept\" });");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoAttributedTypes_EmitsNothingAsync() {
    var result = GeneratorTestHelper.RunGenerator<AuditEventRegistryGenerator>(
      "namespace MyApp; public record Plain : Whizbang.Core.IEvent;");

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "AuditEventRegistry.g.cs")).IsNull();
  }
}
