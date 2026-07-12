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
}
