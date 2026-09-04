using System.Text.RegularExpressions;
using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the global using alias transformer.
/// </summary>
/// <remarks>
/// These are <c>global</c> usings, so anything this transformer emits applies to every file in
/// the migrated project. A wrong replacement therefore does not produce one broken line -- it
/// produces a project that will not compile at all. That is why the mapping table is checked
/// against the real Whizbang assembly rather than against a hardcoded expectation: a string
/// literal in a dictionary cannot go stale silently if a test resolves it.
/// </remarks>
/// <tests>Whizbang.Migrate/Transformers/GlobalUsingAliasTransformer.cs:*</tests>
public class GlobalUsingAliasTransformerTests {

  private static async Task<string> _replacementForAsync(string martenOrWolverineType) {
    var transformer = new GlobalUsingAliasTransformer();
    var source = $"global using LegacyAlias = {martenOrWolverineType};\n";

    var result = await transformer.TransformAsync(source, "GlobalUsings.cs");

    var match = Regex.Match(result.TransformedCode, @"global using LegacyAlias = ([^;]+);");
    return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
  }

  [Test]
  [Arguments("Marten.Events.IEvent")]
  [Arguments("Marten.IDocumentStore")]
  [Arguments("Wolverine.IMessageBus")]
  [Arguments("Wolverine.MessageContext")]
  public async Task TransformAsync_EveryReplacementResolvesToARealTypeAsync(string legacyType) {
    // The guard that matters. Each replacement is a bare string in a dictionary, and nothing
    // else checks it against the library it names. Two of them pointed at
    // Whizbang.Core.Messaging.MessageEnvelope -- a type that exists in neither that namespace
    // nor in any non-generic form -- which turned a global using into a project-wide build
    // failure. Resolving the emitted name against the real assembly makes that undetectable
    // drift impossible.
    var replacement = await _replacementForAsync(legacyType);

    await Assert.That(replacement).IsNotEmpty()
      .Because($"{legacyType} is expected to map to a Whizbang type, not be dropped");

    var resolved = typeof(Whizbang.Core.IDispatcher).Assembly.GetType(replacement);
    await Assert.That(resolved).IsNotNull()
      .Because($"the alias emits 'global using X = {replacement};' and that type has to exist");
  }

  [Test]
  public async Task TransformAsync_NonGenericMartenEvent_MapsToTheNonGenericEnvelopeAsync() {
    // MessageEnvelope is generic-only, so aliasing it without type arguments cannot compile.
    // The non-generic analogue of Marten's IEvent is the IMessageEnvelope interface.
    var replacement = await _replacementForAsync("Marten.Events.IEvent");

    await Assert.That(replacement).IsEqualTo("Whizbang.Core.Observability.IMessageEnvelope");
  }

  [Test]
  public async Task TransformAsync_FileWithoutTargetAliases_IsLeftByteForByteAsync() {
    var transformer = new GlobalUsingAliasTransformer();
    const string source = """
      global using System;
      global using MyAlias = System.Collections.Generic.List<int>;
      """;

    var result = await transformer.TransformAsync(source, "GlobalUsings.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source);
    await Assert.That(result.Changes).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_AliasWithNoEquivalent_IsRemovedAndWarnedAboutAsync() {
    // IDocumentSession has no Whizbang counterpart. Keeping the alias would reference a package
    // that is going away, so it goes -- but every use site now breaks, and the warning is the
    // only thing telling the operator where to look.
    var transformer = new GlobalUsingAliasTransformer();
    const string source = "global using Session = Marten.IDocumentSession;\n";

    var result = await transformer.TransformAsync(source, "GlobalUsings.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("Marten.IDocumentSession");
    await Assert.That(result.Warnings.Any(w => w.Contains("Session", StringComparison.Ordinal))).IsTrue()
      .Because("removing an alias silently would leave the operator hunting compile errors");
  }

  [Test]
  public async Task TransformAsync_GenericMartenEvent_IsRemovedRatherThanRewrittenAsync() {
    // IEvent<T> normalizes to the generic key, which maps to nothing: callers are expected to
    // use MessageEnvelope<T> directly rather than through an alias.
    var transformer = new GlobalUsingAliasTransformer();
    const string source = "global using OrderEvent = Marten.Events.IEvent<Order>;\n";

    var result = await transformer.TransformAsync(source, "GlobalUsings.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("Marten.Events.IEvent");
    await Assert.That(result.Warnings.Count).IsGreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task TransformAsync_UnknownMartenType_IsKeptAndFlaggedAsync() {
    // An unrecognized type is not guessed at. Keeping it means the build fails at that alias
    // rather than somewhere subtler, and the warning names it for review.
    var transformer = new GlobalUsingAliasTransformer();
    const string source = "global using Weird = Marten.Something.Unmapped;\n";

    var result = await transformer.TransformAsync(source, "GlobalUsings.cs");

    await Assert.That(result.TransformedCode).Contains("Marten.Something.Unmapped")
      .Because("guessing a replacement for an unknown type would be worse than leaving it");
    await Assert.That(result.Warnings.Any(w => w.Contains("unknown", StringComparison.OrdinalIgnoreCase))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_NonGlobalAlias_IsLeftAloneAsync() {
    // A file-scoped alias is not this transformer's concern; only global ones have project-wide
    // reach and only they are rewritten here.
    var transformer = new GlobalUsingAliasTransformer();
    const string source = "using Bus = Wolverine.IMessageBus;\n";

    var result = await transformer.TransformAsync(source, "Service.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(source);
  }

  [Test]
  public async Task TransformAsync_UnrelatedGlobalUsings_SurviveAlongsideARewriteAsync() {
    var transformer = new GlobalUsingAliasTransformer();
    const string source = """
      global using System;
      global using Bus = Wolverine.IMessageBus;
      global using System.Linq;
      """;

    var result = await transformer.TransformAsync(source, "GlobalUsings.cs");

    await Assert.That(result.TransformedCode).Contains("global using System;");
    await Assert.That(result.TransformedCode).Contains("global using System.Linq;");
    await Assert.That(result.TransformedCode).Contains("Whizbang.Core.IDispatcher");
  }
}
