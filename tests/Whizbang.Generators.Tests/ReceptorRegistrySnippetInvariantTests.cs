extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using TemplateUtilities = shared::Whizbang.Generators.Shared.Utilities.TemplateUtilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Locks the invariant that keeps <see cref="ReceptorDiscoveryGenerator"/> on its template path.
/// </summary>
/// <remarks>
/// The generator builds each registry entry by lifting the <c>ReceptorInfo(...)</c> constructor
/// call out of an embedded snippet and substituting placeholders into it. When that lift fails it
/// falls back to a hand-rolled StringBuilder that emits the same call from scratch — a second,
/// independent copy of the entry shape that no snippet edit ever updates.
///
/// <para>
/// So the fallback is not a safety net so much as a silent fork: a snippet edited to drop or rename
/// the marker would keep compiling and keep generating, but every receptor entry would come from
/// the stale hand-rolled version instead, and the divergence would only surface as wrong runtime
/// behavior in a consumer's dispatch. These tests pin the marker in each of the four snippets so
/// that edit fails here, loudly, instead.
/// </para>
/// </remarks>
public class ReceptorRegistrySnippetInvariantTests {

  private static readonly System.Reflection.Assembly _generatorsAssembly =
    typeof(MessageRegistryGenerator).Assembly;

  private const string SNIPPET_FILE = "DispatcherSnippets.cs";
  private const string RECEPTOR_INFO_MARKER = "new global::Whizbang.Core.Messaging.ReceptorInfo(";

  /// <summary>The four snippets <c>_selectSnippet</c> can return, one per receptor shape.</summary>
  public static IEnumerable<Func<string>> SnippetRegions() {
    yield return () => "RECEPTOR_REGISTRY_ROUTING_SNIPPET";
    yield return () => "RECEPTOR_REGISTRY_VOID_ROUTING_SNIPPET";
    yield return () => "RECEPTOR_REGISTRY_TRACED_ROUTING_SNIPPET";
    yield return () => "RECEPTOR_REGISTRY_TRACED_VOID_ROUTING_SNIPPET";
  }

  [Test]
  [MethodDataSource(nameof(SnippetRegions))]
  public async Task EverySnippet_ExistsAsync(string regionName) {
    // ExtractSnippet reports a missing region by returning an error string rather than throwing,
    // so a renamed region would otherwise reach the marker check as a confusing failure.
    var snippet = TemplateUtilities.ExtractSnippet(_generatorsAssembly, SNIPPET_FILE, regionName);

    await Assert.That(snippet).IsNotNullOrEmpty();
    await Assert.That(snippet).DoesNotContain("not found")
      .Because($"region {regionName} must exist in {SNIPPET_FILE} — the generator reads it by name");
  }

  [Test]
  [MethodDataSource(nameof(SnippetRegions))]
  public async Task EverySnippet_CarriesTheReceptorInfoMarkerAsync(string regionName) {
    var snippet = TemplateUtilities.ExtractSnippet(_generatorsAssembly, SNIPPET_FILE, regionName);

    await Assert.That(snippet).Contains(RECEPTOR_INFO_MARKER)
      .Because("without this exact marker the generator abandons the template and hand-rolls the "
             + "entry instead — same output today, silently divergent the moment the snippet changes");
  }

  [Test]
  [MethodDataSource(nameof(SnippetRegions))]
  public async Task EverySnippet_ClosesTheReceptorInfoCallAsync(string regionName) {
    // The lift matches parentheses from the marker forward. An unbalanced call would run off the
    // end of the snippet and return null, dropping the generator onto the fallback just as a
    // missing marker would.
    var snippet = TemplateUtilities.ExtractSnippet(_generatorsAssembly, SNIPPET_FILE, regionName);
    var start = snippet.IndexOf(RECEPTOR_INFO_MARKER, StringComparison.Ordinal);

    var depth = 0;
    var closedAt = -1;
    for (var i = start; i >= 0 && i < snippet.Length; i++) {
      if (snippet[i] == '(') {
        depth++;
      } else if (snippet[i] == ')') {
        depth--;
        if (depth == 0) {
          closedAt = i;
          break;
        }
      }
    }

    await Assert.That(closedAt).IsGreaterThan(start)
      .Because("the constructor call must balance inside the snippet or the extraction returns null");
  }

  [Test]
  [MethodDataSource(nameof(SnippetRegions))]
  public async Task EverySnippet_CarriesThePlaceholdersTheGeneratorSubstitutesAsync(string regionName) {
    // Substitution is a plain string replace: a placeholder that no longer appears is not an
    // error, it is a value that silently never gets filled in. Both of these are required on
    // every shape — the message type the entry routes, and the receptor class it resolves.
    var snippet = TemplateUtilities.ExtractSnippet(_generatorsAssembly, SNIPPET_FILE, regionName);

    await Assert.That(snippet).Contains("__MESSAGE_TYPE__");
    await Assert.That(snippet).Contains("__RECEPTOR_CLASS__");
  }
}
