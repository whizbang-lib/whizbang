using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for two <c>Whizbang.Generators.Utilities.EphemeralResolver</c> branches nothing else
/// exercises. <c>EphemeralResolver</c> is <c>internal static</c> and this project's
/// <c>InternalsVisibleTo</c> is deliberately disabled (see <c>Whizbang.Generators/AssemblyInfo.cs</c> —
/// it collides with PolySharp polyfills), so it can only be driven indirectly through the two
/// generator-side consumers that call it: <see cref="EphemeralAnalyzer"/> (WHIZ134) and
/// <see cref="MessageTypeCatalogGenerator"/>.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Utilities/EphemeralResolver.cs</code-under-test>
public class EphemeralResolverCoverageTests {

  // IsAmbiguousComposition resolves own type -> base type -> interfaces, most-specific wins (per the
  // class's own remarks). The base-type early exit is what makes that ordering real: without it, a type
  // whose BASE carries an explicit [Ephemeral] but that ALSO happens to implement two sibling interface
  // profiles that disagree would fall through into the interface-carrier check and get wrongly flagged
  // WHIZ134 — a hard compile error for code that is already unambiguously resolved by its base class.
  // Every existing WHIZ134 test resolves the tie via the type's OWN attribute or via interfaces only;
  // none inherit from an [Ephemeral] BASE while also carrying conflicting interfaces, so this exit was
  // never actually exercised.
  [Test]
  [RequiresAssemblyFiles]
  public async Task BaseTypeEphemeral_WithConflictingInterfaceProfiles_NoWHIZ134Async() {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
      public interface IPresenceProfile : IEvent { }
      [Ephemeral(Destruction = Destruction.AfterTtl, Storage = TransientStorage.TtlRow)]
      public interface ISessionProfile : IEvent { }
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.PersistedRow)]
      public abstract record ResolvedBase : IEvent;
      public record InheritsResolutionFromBase : ResolvedBase, IPresenceProfile, ISessionProfile;
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ134")).IsFalse()
      .Because("the base class's own [Ephemeral] already resolves the type unambiguously — the "
             + "conflicting sibling interfaces below it in the walk must never be consulted");
  }

  // _enumArgName matches a named argument's constant value against the enum's declared field
  // constants by equality. Every existing test supplies a real enum member, so the match always
  // succeeds; none cover what happens when the supplied value does not correspond to ANY member (an
  // explicit numeric cast outside the enum's defined range — legal C#, and something a careless
  // consumer can write). If the no-match case silently propagated the raw value instead of falling
  // back to the documented default, the generated catalog would carry a destruction mode that doesn't
  // correspond to any real strategy, and the runtime reaper would not know how to destroy the event.
  [Test]
  [RequiresAssemblyFiles]
  public async Task Resolve_DestructionValueMatchesNoEnumMember_FallsBackToDefaultAsync() {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      [Ephemeral(Destruction = (Destruction)99)]
      public record OddCastEvent : IEvent;
      """;

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();
    var maybeCode = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(maybeCode).IsNotNull();
    var code = maybeCode!;

    await Assert.That(code).Contains("typeof(global::MyApp.OddCastEvent)");
    await Assert.That(code).Contains("Destruction.WhenConsumed")
      .Because("an out-of-range Destruction cast matches no declared enum member; the resolver must "
             + "fall back to the documented default (WhenConsumed) rather than stamp an unmatched "
             + "value onto the catalog entry that drives the runtime reaper");
  }
}
