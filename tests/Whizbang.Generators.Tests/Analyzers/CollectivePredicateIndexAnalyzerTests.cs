using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ309: a collective predicate that filters a perspective field with no index of its own.
/// </summary>
/// <remarks>
/// <para>
/// A collective apply is one <c>UPDATE</c> whose <c>WHERE</c> is the handler's predicate, compiled to
/// an extraction from the stored document (<c>data -&gt;&gt; 'OverlayId' = @p</c>). Only an index over
/// that extraction answers it. The whole-document index does not, so declaring
/// <c>MatchOnAnyField</c> is no answer, and nothing creates an index at apply time any more. Without
/// <c>[Indexed]</c>, every apply reads every row of the table.
/// </para>
/// <para>
/// The warning can be turned off for a whole build with the <c>WhizbangCollectiveIndexWarning</c>
/// property, and for one perspective, model or field with a reasoned
/// <c>[SuppressIndexAdvisory]</c>.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz309</docs>
[Category("Analyzers")]
public class CollectivePredicateIndexAnalyzerTests {
  private const string SWITCH = "build_property.WhizbangCollectiveIndexWarning";

  private static string _source(
      string predicate, string modelAttribute = "", string perspectiveAttribute = "", string handlerAttribute = "[CollectiveApplyFor]",
      string assemblyAttribute = "") => $$"""
      using System;
      using System.Collections.Generic;
      using System.Linq;
      using System.Linq.Expressions;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Messaging;
      using Whizbang.Core.Perspectives;

      {{assemblyAttribute}}

      namespace TestApp;

      {{modelAttribute}}
      public record OverlayModel {
        [StreamId]
        public Guid Id { get; init; }

        public Guid OverlayId { get; init; }

        [Indexed]
        public string Status { get; init; } = "";

        [Indexed(caseInsensitive: true)]
        public string Region { get; init; } = "";

        [Indexed(IndexKinds.Substring)]
        public string Memo { get; init; } = "";

        [PhysicalField]
        public string Plain { get; init; } = "";

        [PhysicalField]
        [Indexed]
        public string Code { get; init; } = "";

        [PhysicalField(Unique = true)]
        public string Serial { get; init; } = "";

        [SuppressIndexAdvisory("one row per overlay")]
        public string Rare { get; init; } = "";

        public int Ordinal { get; init; }
      }

      public record PositionalModel(Guid Batch, string Name);

      public record StatusModel {
        public string Status { get; init; } = "";
      }

      public sealed record OverlayRemoved : CollectiveEventBase {
        public Guid OverlayId { get; init; }
      }

      public sealed record Spec<TModel>(
          Expression<Action<ICollectiveSetters<TModel>>> Setters,
          Expression<Func<PerspectiveRow<TModel>, bool>>? Where = null) : ICollectiveSpec<TModel> where TModel : class;

      {{perspectiveAttribute}}
      public sealed class OverlayPerspective {
        {{handlerAttribute}}
        public ICollectiveSpec<OverlayModel> Remove(OverlayRemoved e, ICollectiveQuery q) {
          var id = e.OverlayId;
          var ids = new List<Guid> { id };
          return new Spec<OverlayModel>(
            Setters: s => s.SetProperty(o => o.Memo, "removed"),
            Where: r => {{predicate}});
        }
      }
      """;

  private static async Task<List<Diagnostic>> _whiz309Async(string source, Dictionary<string, string>? globalOptions = null) =>
    [.. (await AnalyzerTestHelper.GetDiagnosticsAsync<CollectivePredicateIndexAnalyzer>(source, additionalFiles: null, globalOptions: globalOptions))
      .Where(d => d.Id == "WHIZ309")];

  // ========================================
  // Reported
  // ========================================

  /// <summary>
  /// The case the warning exists for: a collective equality on a field nothing indexes, which scans
  /// the table on every apply.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnEqualityOnAnUnindexedField_WarnsAsync() {
    var reported = await _whiz309Async(_source("r.Data.OverlayId == id"));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("OverlayModel.OverlayId");
    await Assert.That(message).Contains("[Indexed]");
    await Assert.That(message).Contains("[SuppressIndexAdvisory(\"reason\")]");
  }

  /// <summary>
  /// Every shape the collective compiler emits reads the field through an extraction, so each one
  /// needs the field's own index, whatever the comparison.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("r.Data.Ordinal < 5", "OverlayModel.Ordinal")]
  [Arguments("ids.Contains(r.Data.OverlayId)", "OverlayModel.OverlayId")]
  [Arguments("r.Data.Plain == \"x\"", "OverlayModel.Plain")]
  [Arguments("r.Data.Region == \"x\"", "OverlayModel.Region")]
  [Arguments("r.Data.Memo == \"x\"", "OverlayModel.Memo")]
  [Arguments("r.Scope.TenantId == \"t\" && r.Data.OverlayId == id", "OverlayModel.OverlayId")]
  [Arguments("q.Of<StatusModel>().Any(s => s.Id == r.Id && s.Data.Status == \"x\")", "StatusModel.Status")]
  public async Task EveryPredicateShapeOnAnUnindexedField_WarnsAsync(string predicate, string field) {
    var reported = await _whiz309Async(_source(predicate));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains(field);
  }

  /// <summary>
  /// The whole-document index answers containment, not extraction, so declaring that queries match
  /// on any field does not answer a collective predicate.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task MatchingOnAnyField_DoesNotAnswerACollectivePredicateAsync() {
    var reported = await _whiz309Async(_source("r.Data.OverlayId == id", modelAttribute: "[PerspectiveQueries(MatchOnAnyField = true)]"));

    await Assert.That(reported).Count().IsEqualTo(1);
  }

  /// <summary>
  /// The diagnostic carries the field's declaration as its additional location, which is where the
  /// code fix writes <c>[Indexed]</c>.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task TheFieldsDeclarationIsTheAdditionalLocationAsync() {
    var source = _source("r.Data.OverlayId == id");
    var reported = await _whiz309Async(source);

    await Assert.That(reported[0].AdditionalLocations).Count().IsEqualTo(1);
    var declared = reported[0].AdditionalLocations[0];
    await Assert.That(source.Substring(declared.SourceSpan.Start, declared.SourceSpan.Length))
      .IsEqualTo("public Guid OverlayId { get; init; }");
  }

  // ========================================
  // Not reported
  // ========================================

  /// <summary>Each field with an index the extraction can use, and each row column, is left alone.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("r.Data.Status == \"x\"")]
  [Arguments("r.Data.Code == \"x\"")]
  [Arguments("r.Data.Serial == \"x\"")]
  [Arguments("r.Data.Id == id")]
  [Arguments("r.Id == id")]
  [Arguments("r.Scope.TenantId == \"t\"")]
  [Arguments("r.Data.Rare == \"x\"")]
  [Arguments("r.Data.GetHashCode() == 0")]
  public async Task AnIndexedFieldOrARowColumn_DoesNotWarnAsync(string predicate) {
    await Assert.That(await _whiz309Async(_source(predicate))).IsEmpty();
  }

  /// <summary>A model-wide index declaration indexes every field, including the filtered one.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AModelThatIndexesEveryField_DoesNotWarnAsync() {
    await Assert.That(await _whiz309Async(_source("r.Data.OverlayId == id", modelAttribute: "[IndexAllFields]"))).IsEmpty();
  }

  /// <summary>
  /// The same predicate outside a collective handler is a lens filter, which WHIZ302, WHIZ307 and
  /// WHIZ308 already answer.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task OutsideACollectiveHandler_DoesNotWarnAsync() {
    await Assert.That(await _whiz309Async(_source("r.Data.OverlayId == id", handlerAttribute: ""))).IsEmpty();
  }

  /// <summary>A predicate written outside any method, as a field initializer, belongs to no handler.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task APredicateOutsideAnyMethod_DoesNotWarnAsync() {
    var source = _source("true").Replace(
      "public sealed class OverlayPerspective {",
      "public sealed class OverlayPerspective {\n  private static readonly Expression<Func<PerspectiveRow<OverlayModel>, bool>> _cohort = r => r.Data.OverlayId == Guid.Empty;",
      StringComparison.Ordinal);

    await Assert.That(await _whiz309Async(source)).IsEmpty();
  }

  /// <summary>A member called Data on something other than a perspective row is not a perspective field.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("new Holder().Data.Name")]
  [Arguments("new Box<Payload>().Data.Name")]
  [Arguments("new Raw().Data.Name")]
  public async Task ADataMemberOffAnotherType_DoesNotWarnAsync(string access) {
    var source = _source("true").Replace(
      "var id = e.OverlayId;",
      $"var id = e.OverlayId;\n    var unused = {access};",
      StringComparison.Ordinal) + """

      public class Payload { public string Name { get; init; } = ""; }
      public class Holder { public Payload Data { get; init; } = new(); }
      public class Box<T> where T : new() { public T Data { get; init; } = new(); }
      public class Raw { public Payload Data = new(); }
      """;

    await Assert.That(await _whiz309Async(source)).IsEmpty();
  }

  /// <summary>
  /// A handler generic over its model names no model, so there is no field whose index to ask about.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AHandlerGenericOverItsModel_DoesNotWarnAsync() {
    var source = _source("true").Replace(
      "public sealed class OverlayPerspective {",
      """
      public sealed class OverlayPerspective {
        [CollectiveApplyFor]
        public ICollectiveSpec<T> Any<T>(OverlayRemoved e) where T : class =>
          new Spec<T>(s => s.ToString(), r => r.Data.GetHashCode() == 0);
      """,
      StringComparison.Ordinal);

    await Assert.That(await _whiz309Async(source)).IsEmpty();
  }

  // ========================================
  // Suppressed
  // ========================================

  /// <summary>
  /// A reasoned suppression on the perspective, the model or the assembly records the decision, and
  /// ends the warning.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AReasonedSuppression_EndsTheWarningAsync() {
    const string ATTRIBUTE = "[SuppressIndexAdvisory(\"a few hundred rows, ever\")]";

    await Assert.That(await _whiz309Async(_source("r.Data.OverlayId == id", perspectiveAttribute: ATTRIBUTE))).IsEmpty()
      .Because("suppressing on the perspective silences only its collective predicates");
    await Assert.That(await _whiz309Async(_source("r.Data.OverlayId == id", modelAttribute: ATTRIBUTE))).IsEmpty();
    await Assert.That(await _whiz309Async(_source("r.Data.OverlayId == id",
      assemblyAttribute: "[assembly: SuppressIndexAdvisory(\"a few hundred rows, ever\")]"))).IsEmpty();
  }

  /// <summary>A blank reason is not a decision, so it suppresses nothing.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ABlankReasonOnThePerspective_DoesNotSuppressAsync() {
    var reported = await _whiz309Async(_source("r.Data.OverlayId == id", perspectiveAttribute: "[SuppressIndexAdvisory(\" \")]"));

    await Assert.That(reported).Count().IsEqualTo(1);
  }

  /// <summary>The build property turns the warning off for the whole compilation.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("false")]
  [Arguments(" False ")]
  public async Task TheBuildProperty_TurnsTheWarningOffAsync(string value) {
    var reported = await _whiz309Async(_source("r.Data.OverlayId == id"), new Dictionary<string, string> { [SWITCH] = value });

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>Any other value of the build property leaves the warning on.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("true")]
  [Arguments("")]
  public async Task AnyOtherValueOfTheBuildProperty_LeavesTheWarningOnAsync(string value) {
    var reported = await _whiz309Async(_source("r.Data.OverlayId == id"), new Dictionary<string, string> { [SWITCH] = value });

    await Assert.That(reported).Count().IsEqualTo(1);
  }
}
