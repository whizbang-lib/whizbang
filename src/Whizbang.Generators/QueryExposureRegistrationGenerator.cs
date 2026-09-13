using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Records, for the running process, which perspective models a request can shape the query for.
/// </summary>
/// <remarks>
/// <para>
/// WHIZ306 reports the same exposure at build time, and the two are not redundant. A build warning is
/// read once by whoever is compiling; the registry is what lets the process itself answer "can a
/// request order by any field of this model", which is the question worth asking against a live table
/// where the row count is known and a missing index has a measurable cost.
/// </para>
/// <para>
/// Emitted per assembly from a module initializer, with no reflection, so it survives trimming and
/// native compilation. Per assembly because a generator sees only its own compilation: the exposure
/// is declared where the surface is, usually the host, while the model is declared in a library that
/// knows nothing about it, so neither side could answer alone and the registrations compose as
/// assemblies load.
/// </para>
/// <para>
/// <strong>Only the marker and the built-in names reach the registry.</strong> An attribute a
/// consumer named through the analyzer option raises the build warning but is not registered here,
/// because reading that option in the syntax transform would require routing every candidate through
/// the compilation provider to keep the semantic model available. Marking the attribute is what gets
/// both, and it is the recommended path for that reason.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Generators.Tests/QueryExposureRegistrationGeneratorTests.cs</tests>
[Generator]
public sealed class QueryExposureRegistrationGenerator : IIncrementalGenerator {
  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var exposures = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => _isPotentialSurface(node),
        transform: static (ctx, ct) => _extract(ctx, ct))
      .Where(static found => found is not null)
      .Select(static (found, _) => found!.Value);

    context.RegisterSourceOutput(exposures.Collect(), static (spc, found) => _emit(spc, found));
  }

  /// <summary>
  /// A type or member that carries at least one attribute, which is the cheapest thing an exposure
  /// cannot be without.
  /// </summary>
  private static bool _isPotentialSurface(SyntaxNode node) => node switch {
    InterfaceDeclarationSyntax { AttributeLists.Count: > 0 } => true,
    ClassDeclarationSyntax { AttributeLists.Count: > 0 } => true,
    RecordDeclarationSyntax { AttributeLists.Count: > 0 } => true,
    MethodDeclarationSyntax { AttributeLists.Count: > 0 } => true,
    PropertyDeclarationSyntax { AttributeLists.Count: > 0 } => true,
    _ => false,
  };

  private static ExposedModel? _extract(GeneratorSyntaxContext context, CancellationToken ct) {
    // Null-tolerant rather than guarded: every node shape the predicate yields is a declaration, so
    // a null symbol is not a case this has seen, and a guard for it would be a line no test could
    // honestly cover. An empty attribute list reaches the same answer by the same path.
    var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node, ct);

    var exposure = SortableExposureDiscovery.ExposureOf(symbol?.GetAttributes() ?? [], []);
    if (exposure == 0) {
      return null;
    }

    if (SortableExposureDiscovery.ModelExposedBy(symbol) is not { } model) {
      return null;
    }

    // Carried from here because it cannot be recomputed at runtime without reading the model's
    // attributes, and because it is what lets a recorded decision stand down the runtime advisory as
    // well as the build warning: a suppressed or fully indexed model registers no unaccounted fields.
    var unattributed = SortableExposureDiscovery.UnattributedFields(model);

    return new ExposedModel(
      TypeNameUtilities.FullyQualified(model), exposure, string.Join("\u001f", unattributed));
  }

  private static void _emit(SourceProductionContext context, ImmutableArray<ExposedModel> exposures) {
    if (exposures.IsDefaultOrEmpty) {
      return;
    }

    // One registration per model, combining what each surface offers. Several surfaces over one model
    // is the norm, and emitting a call apiece would make the generated file grow with the number of
    // endpoints while saying the same thing.
    var combined = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var fields = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    foreach (var exposure in exposures) {
      combined[exposure.ModelTypeName] =
        combined.TryGetValue(exposure.ModelTypeName, out var held) ? held | exposure.Exposure : exposure.Exposure;

      if (!fields.TryGetValue(exposure.ModelTypeName, out var names)) {
        names = new SortedSet<string>(StringComparer.Ordinal);
        fields[exposure.ModelTypeName] = names;
      }
      foreach (var name in exposure.UnindexedFields.Split('\u001f')) {
        if (name.Length > 0) {
          names.Add(name);
        }
      }
    }

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>Records this assembly's request-composed query exposures at load.</summary>");
    sb.AppendLine("internal static class QueryExposureRegistrations {");
    sb.AppendLine("  [global::System.Runtime.CompilerServices.ModuleInitializer]");
    sb.AppendLine("  internal static void Register() {");

    foreach (var entry in combined) {
      sb.Append("    global::Whizbang.Core.Perspectives.QueryExposureRegistry.Register<")
        .Append(entry.Key)
        .Append(">(")
        .Append(_renderExposure(entry.Value));

      foreach (var name in fields[entry.Key]) {
        sb.Append(", \"").Append(name).Append('"');
      }

      sb.AppendLine(");");
    }

    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("QueryExposureRegistrations.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
  }

  /// <summary>The exposure as named flags, so the generated call reads like the declaration.</summary>
  private static string _renderExposure(int exposure) {
    var names = new List<string>();
    if ((exposure & 1) != 0) { names.Add("Filtering"); }
    if ((exposure & 2) != 0) { names.Add("Ordering"); }
    if ((exposure & 4) != 0) { names.Add("Expression"); }
    if (names.Count == 0) { names.Add("None"); }

    return string.Join(" | ", names.Select(static n => "global::Whizbang.Core.Perspectives.QueryExposures." + n));
  }

  /// <summary>One model and what a request can shape about it, as a value for the generator cache.</summary>
  /// <param name="ModelTypeName">The model's fully qualified name.</param>
  /// <param name="Exposure">The <c>QueryExposures</c> flags, as an integer.</param>
  /// <param name="UnindexedFields">
  /// The unaccounted field names, joined by a unit separator. A single string rather than an array
  /// because the generator cache compares by value and an array compares by reference, which would
  /// make every candidate look changed on every build.
  /// </param>
  private readonly record struct ExposedModel(string ModelTypeName, int Exposure, string UnindexedFields);
}
