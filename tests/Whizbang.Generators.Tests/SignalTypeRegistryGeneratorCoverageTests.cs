using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="SignalTypeRegistryGenerator"/> targeting the discovery-side
/// skip branches (abstract, open-generic, non-public/internal, non-<c>ISignal</c>) and the default
/// wire-name path (an attribute present but not <c>[WireName]</c>) that
/// <c>SignalTypeRegistryGeneratorTests.cs</c> does not exercise.
/// </summary>
[Category("SourceGenerators")]
public class SignalTypeRegistryGeneratorCoverageTests {

  /// <summary>
  /// An abstract type can never be the concrete runtime type of an emitted signal — <c>typeof(T)</c>
  /// in the generated entry needs a closed, instantiable type. Registering an abstract signal would
  /// produce code that references a type nobody can ever actually deliver.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AbstractSignalType_SkipsExtractionAsync() {
    const string source = @"
using Whizbang.Core.Signals;

namespace App.Signals {
  public abstract record AbstractSignal : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }
}";

    var result = GeneratorTestHelper.RunGenerator<SignalTypeRegistryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "SignalTypeSource.g.cs");

    await Assert.That(code).IsNull()
      .Because("an abstract signal type has no concrete runtime instance and must not be registered");
  }

  /// <summary>
  /// An open-generic type (<c>Signal&lt;T&gt;</c>) has no single closed <c>typeof(T)</c> the generated
  /// fragment could reference — every consumer would need a different closed instantiation, which the
  /// generator cannot enumerate from the declaration alone.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_OpenGenericSignalType_SkipsExtractionAsync() {
    const string source = @"
using Whizbang.Core.Signals;

namespace App.Signals {
  public class GenericSignal<T> : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }
}";

    var result = GeneratorTestHelper.RunGenerator<SignalTypeRegistryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "SignalTypeSource.g.cs");

    await Assert.That(code).IsNull()
      .Because("an open-generic definition can't be listed by a closed typeof — there is no single instantiation to register");
  }

  /// <summary>
  /// The generated <c>GeneratedSignalTypeSource</c> fragment lives in the SAME assembly as the signal
  /// types it lists, so a <c>private</c> nested signal is technically reachable from it — but treating
  /// it as public API would leak an implementation-detail type into the cross-assembly signal registry.
  /// Only public/internal signals are eligible.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PrivateNestedSignalType_SkipsExtractionAsync() {
    const string source = @"
using Whizbang.Core.Signals;

namespace App.Signals {
  public class Container {
    private class NestedSignal : ISignal {
      public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
      public static SignalTargeting Targeting => SignalTargeting.Broadcast;
    }
  }
}";

    var result = GeneratorTestHelper.RunGenerator<SignalTypeRegistryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "SignalTypeSource.g.cs");

    await Assert.That(code).IsNull()
      .Because("a private nested type is not eligible for the cross-assembly signal registry regardless of ISignal conformance");
  }

  /// <summary>
  /// A type with a base list that does not include <c>ISignal</c> (e.g. it implements some other
  /// interface) must not be swept into the signal registry just because it has a base list at all —
  /// the syntactic pre-filter is broad on purpose, and the semantic <c>ISignal</c> check is what
  /// actually decides membership.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NonSignalTypeWithBaseList_SkipsExtractionAsync() {
    const string source = @"
namespace App.Signals {
  public class NotASignal : System.IDisposable {
    public void Dispose() { }
  }
}";

    var result = GeneratorTestHelper.RunGenerator<SignalTypeRegistryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "SignalTypeSource.g.cs");

    await Assert.That(code).IsNull()
      .Because("a type that merely has a base list, without implementing ISignal, must not be registered as a signal");
  }

  /// <summary>
  /// Two behaviors at once: (1) a signal carrying some OTHER attribute (not [WireName]) must still
  /// default its wire name to its fully-qualified type name rather than being skipped or crashing on
  /// the unrelated attribute; (2) the emitted entries are ordered by fully-qualified name regardless of
  /// declaration order, which is what makes the generator's output byte-for-byte stable across
  /// incremental rebuilds — an unstable order would make every rebuild look like a diff to source
  /// control and to any tooling that hashes the generated file.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_SignalsWithNonWireNameAttributes_DefaultWireNameAndSortDeterministicallyAsync() {
    const string source = @"
using System;
using Whizbang.Core.Signals;

namespace App.Signals {
  [Obsolete(""legacy"")]
  public readonly record struct ZetaSignal : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  [Obsolete(""legacy"")]
  public readonly record struct AlphaSignal : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }
}";

    var result = GeneratorTestHelper.RunGenerator<SignalTypeRegistryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "SignalTypeSource.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("_entry<global::App.Signals.ZetaSignal>(\"App.Signals.ZetaSignal\")")
      .Because("with no [WireName], the wire name must default to the type's own fully-qualified name");
    await Assert.That(code!).Contains("_entry<global::App.Signals.AlphaSignal>(\"App.Signals.AlphaSignal\")")
      .Because("an unrelated attribute like [Obsolete] must not be mistaken for [WireName] or block default wire-name assignment");

    var alphaIndex = code!.IndexOf("_entry<global::App.Signals.AlphaSignal>", StringComparison.Ordinal);
    var zetaIndex = code!.IndexOf("_entry<global::App.Signals.ZetaSignal>", StringComparison.Ordinal);
    await Assert.That(alphaIndex).IsLessThan(zetaIndex)
      .Because("entries must be ordered by fully-qualified name (ordinal) regardless of declaration order, or the generated file's byte content would vary run-to-run for the same set of signals");
  }
}
