using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="SignalTypeRegistryGenerator"/> — the cross-assembly signal-type registry
/// fragment generator (Mechanism A).
/// </summary>
[Category("SourceGenerators")]
public class SignalTypeRegistryGeneratorTests {
  private const string SIGNAL_SOURCE = @"
using Whizbang.Core.Signals;

namespace App.Signals {
  public readonly record struct CacheInvalidated(string Region) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }
}";

  [Test]
  public async Task Generator_ConcreteSignal_EmitsSourceWithEntryAndModuleInitializerAsync() {
    var result = GeneratorTestHelper.RunGenerator<SignalTypeRegistryGenerator>(SIGNAL_SOURCE);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "SignalTypeSource.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("GeneratedSignalTypeSource : global::Whizbang.Core.Signals.ISignalTypeSource");
    await Assert.That(code!).Contains("[ModuleInitializer]");
    await Assert.That(code!).Contains("global::Whizbang.Core.Signals.SignalTypeRegistry.Register");
    await Assert.That(code!).Contains("_entry<global::App.Signals.CacheInvalidated>(\"App.Signals.CacheInvalidated\")");
    await Assert.That(code!).Contains("sink.ReceiveAsync<TSignal>(default!, ct)");
  }

  [Test]
  public async Task Generator_NoSignals_EmitsNothingAsync() {
    const string source = @"
namespace App {
  public class NotASignal { }
}";
    var result = GeneratorTestHelper.RunGenerator<SignalTypeRegistryGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "SignalTypeSource.g.cs");
    await Assert.That(code).IsNull();
  }

  [Test]
  public async Task Generator_ConcreteSignal_GeneratesCompilableCodeAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<SignalTypeRegistryGenerator>(SIGNAL_SOURCE);

    await Assert.That(errors.IsEmpty).IsTrue();
  }

  private const string WIRE_NAME_SIGNAL_SOURCE = @"
using Whizbang.Core.Signals;

namespace App.Signals {
  [WireName(""outbox"")]
  public readonly record struct WorkOutboxAvailable : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Targeted;
  }
}";

  [Test]
  public async Task Generator_WireNameAttribute_OverridesDefaultWireNameAsync() {
    var result = GeneratorTestHelper.RunGenerator<SignalTypeRegistryGenerator>(WIRE_NAME_SIGNAL_SOURCE);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "SignalTypeSource.g.cs");
    await Assert.That(code).IsNotNull();
    // The generated entry must use the [WireName] value, not the fully-qualified type name.
    await Assert.That(code!).Contains("_entry<global::App.Signals.WorkOutboxAvailable>(\"outbox\")")
      .Because("the [WireName] attribute overrides the default FQ-name wire-name so signals can interoperate with fixed wire-formats like today's work-signal payloads");
    await Assert.That(code!).DoesNotContain("_entry<global::App.Signals.WorkOutboxAvailable>(\"App.Signals.WorkOutboxAvailable\")");
  }

  [Test]
  public async Task Generator_WireNameAttribute_GeneratesCompilableCodeAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<SignalTypeRegistryGenerator>(WIRE_NAME_SIGNAL_SOURCE);

    await Assert.That(errors.IsEmpty).IsTrue();
  }
}
