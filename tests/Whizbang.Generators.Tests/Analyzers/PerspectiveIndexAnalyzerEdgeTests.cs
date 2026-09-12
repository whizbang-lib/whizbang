using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// The shapes the index analyzers walk past, and the ones they recognize by a spelling.
/// </summary>
/// <remarks>
/// <para>
/// Both analyzers work by walking up the syntax tree from a member access, asking at each step
/// whether what they are inside makes this read a filter. Most of what they meet is neither a filter
/// nor a defect, and walking past it quietly is the whole job: a read in a projection, a lambda
/// assigned to a variable, a field rather than a property. Getting any of those wrong is a warning on
/// a query that is already fine, which is the failure mode that makes an advisory get switched off.
/// </para>
/// <para>
/// Two of these are behaviors rather than boundaries and are worth reading as such. The analyzer
/// recognizes the static <c>string.Equals(member, value, StringComparison.Ordinal)</c> spelling, and
/// it honors a suppression declared on a type the model merely composes.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz302</docs>
/// <docs>operations/diagnostics/whiz303</docs>
[Category("Analyzers")]
public class PerspectiveIndexAnalyzerEdgeTests {
  private const string PRELUDE = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public class EdgeModel {
        [StreamId]
        public Guid EdgeId { get; init; }

        public string Code { get; init; } = string.Empty;

        // A field rather than a property, which is not something an index is declared over.
        public string Loose = string.Empty;
      }

      """;

  private static IEnumerable<Diagnostic> _whiz302(IEnumerable<Diagnostic> diagnostics) =>
    diagnostics.Where(d => d.Id == "WHIZ302");

  /// <summary>
  /// A read that is not a property is walked past, because only a property can be promoted.
  /// </summary>
  /// <remarks>
  /// A public field on a model is unusual and legal. Nothing about it can carry an index declaration,
  /// so the advice the advisory would give has nowhere to land, and reporting it would be a warning
  /// with no fix.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AReadThatIsNotAPropertyIsWalkedPastAsync() {
    var source = PRELUDE + """
      public class EdgeRepository {
        private readonly IQueryable<PerspectiveRow<EdgeModel>> _rows = null!;

        public object Find() => _rows.Where(r => r.Data.Loose == "v").ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("a field cannot be promoted or indexed, so there is no advice to give about one");
  }

  /// <summary>
  /// The static <c>string.Equals</c> spelling with ordinal comparison is an equality, and containment
  /// serves it, so it is not reported.
  /// </summary>
  /// <remarks>
  /// The same comparison as <c>==</c> written another way. Reporting it would tell an author to index
  /// a field whose filter the document index already answers, which is advice that costs an index for
  /// nothing.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task TheStaticOrdinalEqualsSpellingIsServedByContainmentAsync() {
    var source = PRELUDE + """
      public class EdgeRepository {
        private readonly IQueryable<PerspectiveRow<EdgeModel>> _rows = null!;

        public object Find() =>
          _rows.Where(r => string.Equals(r.Data.Code, "v", StringComparison.Ordinal)).ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("it is the same equality as ==, which the document index answers");
  }

  /// <summary>
  /// An <c>Equals</c> with no comparison argument is an ordinary equality too.
  /// </summary>
  /// <remarks>
  /// The comparison argument is what decides the answer when there is one: only ordinal comparison is
  /// what containment performs. With no argument at all the call is plain equality, so it is served
  /// the same way, and treating an absent argument as a non-ordinal one would report a filter that is
  /// already answered.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [SuppressMessage("Globalization", "CA1309:Use ordinal string comparison",
    Justification = "The absence of a StringComparison argument is the shape under test, inside a "
      + "source fixture the analyzer reads as text.")]
  public async Task AnEqualsWithNoComparisonIsAnOrdinaryEqualityAsync() {
    var source = PRELUDE + """
      public class EdgeRepository {
        private readonly IQueryable<PerspectiveRow<EdgeModel>> _rows = null!;

        public object Find() => _rows.Where(r => string.Equals(r.Data.Code, "v")).ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty();
  }

  /// <summary>
  /// A read outside any filtering operator is not a filter.
  /// </summary>
  /// <remarks>
  /// The analyzer reports a read that decides which rows the database has to look at. A read of an
  /// already-materialized row decides nothing, and the walk up the tree ends at the member
  /// declaration without ever meeting an operator.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AReadOutsideAnyOperatorIsNotAFilterAsync() {
    var source = PRELUDE + """
      public class EdgeRepository {
        private readonly PerspectiveRow<EdgeModel> _row = null!;

        public string Read() {
          var value = _row.Data.Code;
          return value;
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("reading a row already in hand decides nothing about which rows were read");
  }

  /// <summary>
  /// A lambda that is not handed to an operator is not a filter either.
  /// </summary>
  /// <remarks>
  /// A predicate stored in a variable may be passed to anything later, or to nothing. The analyzer
  /// only reports what it can see reaching a row-selecting operator, because the alternative is
  /// warning about every lambda that mentions a model.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ALambdaNotHandedToAnOperatorIsNotAFilterAsync() {
    var source = PRELUDE + """
      public class EdgeRepository {
        public Func<PerspectiveRow<EdgeModel>, bool> Build() => r => r.Data.Code == "v";
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("where it goes is not visible here, and warning about every lambda that mentions a "
        + "model would make the advisory worth turning off");
  }

  /// <summary>
  /// A suppression on a base type covers the fields that base declares.
  /// </summary>
  /// <remarks>
  /// The decision belongs where the property is declared. One base carries fields for many models, so
  /// requiring the attribute on each derived model would mean restating one decision per model and
  /// having the reasons drift apart.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ASuppressionOnABaseTypeCoversItsFieldsAsync() {
    var source = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      [SuppressIndexAdvisory("bounded at a few hundred rows by the retention cap")]
      public class AuditStamp {
        public string Reason { get; init; } = string.Empty;
      }

      // Reason is declared on the base, so the suppression has to be found there: this model never
      // mentions it.
      public class ComposingModel : AuditStamp {
        [StreamId]
        public Guid ComposingId { get; init; }
      }

      public class ComposingRepository {
        private readonly IQueryable<PerspectiveRow<ComposingModel>> _rows = null!;

        public object Find() => _rows.Where(r => r.Data.Reason.Contains("ab")).ToList();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics)).IsEmpty()
      .Because("the decision belongs where the property is declared, so the base carries it");

    // The control, and it is what makes the assertion above mean anything: the identical shape with
    // the attribute removed has to be reported. Without this, "no diagnostic" would also be the
    // answer if the analyzer never looked at this shape at all.
    var unsuppressed = source.Replace(
      "[SuppressIndexAdvisory(\"bounded at a few hundred rows by the retention cap\")]\n",
      string.Empty,
      StringComparison.Ordinal);

    var withoutSuppression =
      await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(unsuppressed);

    await Assert.That(_whiz302(withoutSuppression).Select(
        d => d.GetMessage(CultureInfo.InvariantCulture)).Single())
      .Contains("Reason", StringComparison.Ordinal)
      .Because("the same filter without the attribute is exactly what the advisory is for, so if this "
        + "is silent too then the case above proves nothing");
  }

  /// <summary>
  /// A bare boolean filter in query syntax is reported, having been recognized through neither a
  /// comparison nor a call.
  /// </summary>
  /// <remarks>
  /// <c>where r.Data.Flag</c> is a filter with no operator in it at all: the read is the predicate.
  /// Nothing about its shape says whether containment could serve it, so the walk for that question
  /// runs out and the answer is no, which leaves the advisory to report it. A field filtered this way
  /// is read from every row exactly as one compared with <c>==</c> would be.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ABareBooleanFilterInQuerySyntaxIsReportedAsync() {
    var source = """
      using System;
      using System.Linq;
      using Whizbang.Core;
      using Whizbang.Core.Lenses;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public class FlagModel {
        [StreamId]
        public Guid FlagId { get; init; }

        public bool Flag { get; init; }
      }

      public class FlagRepository {
        private readonly IQueryable<PerspectiveRow<FlagModel>> _rows = null!;

        public object Find() => (from r in _rows where r.Data.Flag select r).ToList();

        // Built and materialized separately, which is the same filter with nothing enclosing the
        // query to cut the search short.
        public object FindInTwoSteps() {
          var q = from r in _rows where r.Data.Flag select r;
          return q.ToList();
        }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFilterIndexAnalyzer>(source);

    await Assert.That(_whiz302(diagnostics).Select(d => d.GetMessage(CultureInfo.InvariantCulture)))
      .Count().IsEqualTo(2);
    await Assert.That(_whiz302(diagnostics).Select(d => d.GetMessage(CultureInfo.InvariantCulture)).First())
      .Contains("Flag", StringComparison.Ordinal)
      .Because("the read is the predicate, so it selects rows just as a comparison would, and both "
        + "spellings have to be seen: one ends at the call that materializes it, the other at nothing");
  }

  /// <summary>
  /// Source that does not bind is walked past rather than reported or thrown on.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the ordinary case in an editor, not a curiosity: an analyzer runs on every keystroke, so
  /// it spends much of its life looking at source mid-edit that does not yet compile. A declaration
  /// the semantic model cannot resolve to a symbol is exactly what that looks like.
  /// </para>
  /// <para>
  /// Two things must not happen. It must not throw, because that surfaces as a compiler crash rather
  /// than a squiggle, and it must not report, because a diagnostic about a half-typed declaration is
  /// noise that arrives before the author has finished the thought.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("duplicate property")]
  [Arguments("duplicate type")]
  public async Task SourceThatDoesNotBindIsWalkedPastAsync(string shape) {
    var body = shape switch {
      "duplicate property" => """
        public class ClashModel {
          [Indexed]
          public byte[] Clash { get; init; } = Array.Empty<byte>();

          [Indexed]
          public byte[] Clash { get; init; } = Array.Empty<byte>();
        }
        """,
      "duplicate type" => """
        public class ClashModel {
          [Indexed]
          public byte[] Clash { get; init; } = Array.Empty<byte>();
        }

        public class ClashModel {
          [Indexed]
          public byte[] Clash { get; init; } = Array.Empty<byte>();
        }
        """,
      _ => throw new InvalidOperationException(shape),
    };

    var source = $"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      {body}
      """;

    await Assert.That(async () =>
        await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source))
      .ThrowsNothing()
      .Because("an analyzer that throws on source mid-edit surfaces as a compiler crash");
  }

  /// <summary>
  /// Both analyzers decline a null registration context rather than throwing into the host.
  /// </summary>
  /// <remarks>
  /// An analyzer that throws during registration takes the whole analysis down with it, and the host
  /// reports that as a compiler crash rather than as a diagnostic. Declining is the only safe
  /// behavior, and it costs one comparison at startup.
  /// </remarks>
  [Test]
  public async Task RegistrationWithNoContextIsDeclinedAsync() {
    await Assert.That(() => new JsonIndexDeclarationAnalyzer().Initialize(null!)).ThrowsNothing()
      .Because("throwing during registration surfaces as a compiler crash, not a diagnostic");

    await Assert.That(new JsonIndexDeclarationAnalyzer().SupportedDiagnostics.Length).IsGreaterThan(0)
      .Because("the descriptors have to be reachable whether registration ran or not, or the host "
        + "cannot map a reported id back to its rule");
  }

  /// <summary>
  /// A model declaring an index on a type that cannot carry one is still reported through the
  /// analyzer's own path, which is what replaced the discovery helper that used to answer this.
  /// </summary>
  /// <remarks>
  /// Worth asserting here rather than trusting the WHIZ303 cases alone, because the helper that once
  /// computed this list was removed for having no caller. This pins that the surviving path is the
  /// one doing the work.
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnIndexOnAnUnindexableTypeIsReportedAsync() {
    var source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public class OddModel {
        [StreamId]
        public Guid OddId { get; init; }

        [Indexed]
        public byte[] Blob { get; init; } = Array.Empty<byte>();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<JsonIndexDeclarationAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ303").Select(
        d => d.GetMessage(CultureInfo.InvariantCulture)).Single())
      .Contains("Blob", StringComparison.Ordinal);
  }
}
