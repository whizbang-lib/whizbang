// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ200: an array of records in a perspective model stores the model as one opaque document (#1910).
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework cannot read an array of a complex element back on the mapped path, so the generator stores such a
/// model as one serialized document, and nothing inside it can be indexed. The warning says so where the array is
/// declared, at any depth, and names the collection shapes that keep the document mapped. An array of primitives is a
/// mapped value that reads back, so it is not reported.
/// </para>
/// <para>
/// A perspective model is a type a perspective in the compilation projects, and the opaque form belongs to the Entity
/// Framework driver: a project that does not reference it is not told about a storage choice it never makes. The
/// warning can be turned off for a build with the <c>WhizbangOpaqueArrayWarning</c> property, and for one property
/// with <c>#pragma warning disable WHIZ200</c> or <c>[SuppressMessage]</c>.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz200</docs>
[Category("Analyzers")]
public class PerspectiveModelArrayAnalyzerTests {
  private const string SWITCH_PROPERTY = "WhizbangOpaqueArrayWarning";

  /// <summary>A model named <c>LedgerModel</c> with <paramref name="members"/>, projected by one perspective.</summary>
  private static string _source(string members, string types = "") => $$"""
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics.CodeAnalysis;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public sealed record Tag(Guid TagId, string Label);

    {{types}}

    public record Noted([property: StreamId] Guid Id) : IEvent;

    public class LedgerModel {
      [StreamId] public Guid Id { get; set; }
    {{members}}
    }

    public class LedgerProjection : IPerspectiveFor<LedgerModel, Noted> {
      public LedgerModel Apply(LedgerModel currentData, Noted eventData) => currentData;
    }
    """;

  [RequiresAssemblyFiles]
  private static async Task<List<Diagnostic>> _whiz200Async(
      string source, bool efCore = true, Dictionary<string, string>? properties = null) {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source);
    if (efCore) {
      compilation = compilation.AddReferences(CSharpCompilation.Create("Whizbang.Data.EFCore.Postgres").ToMetadataReference());
    }
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(
      compilation, globalOptions: MSBuildGlobalOptions.For(properties));
    return [.. diagnostics.Where(d => d.Id == "WHIZ200")];
  }

  private static string _locatedAt(Diagnostic diagnostic) =>
    diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);

  // ========================================
  // Reported
  // ========================================

  /// <summary>The case the warning exists for: an array of records at the top of the model.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnArrayOfRecords_IsReportedAtItsProperty_SayingTheModelIsStoredOpaquelyAsync() {
    var reported = await _whiz200Async(_source("  public Tag[] Tags { get; set; } = [];"));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("Tags")
      .Because("reported at the property, so a pragma or a SuppressMessage on it applies");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).IsEqualTo(
      "Property 'Tags' of perspective model 'LedgerModel' is an array of records ('Tag[]'), so the model is stored as one "
      + "opaque document and none of its fields can be indexed. Declare it as List<Tag>, IList<Tag> or "
      + "ImmutableList<Tag> to keep the document mapped and indexable.");
  }

  /// <summary>An array of records inside a collection element is reported at its own property, named by its path.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("public List<Group> Groups { get; set; } = [];")]
  [Arguments("public Group[] Groups { get; set; } = [];")]
  public async Task AnArrayOfRecordsInsideACollectionElement_IsReportedByItsPathAsync(string member) {
    var reported = await _whiz200Async(_source($"  {member}", """
      public sealed record Group {
        public string Name { get; init; } = "";
        public Tag[] Tags { get; init; } = [];
      }
      """));

    var nested = reported.Single(d => d.GetMessage(CultureInfo.InvariantCulture).Contains("'Groups.Tags'", StringComparison.Ordinal));
    await Assert.That(_locatedAt(nested)).IsEqualTo("Tags");
  }

  /// <summary>An array of records under a nested object is reported by its path.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnArrayOfRecordsUnderANestedObject_IsReportedByItsPathAsync() {
    var reported = await _whiz200Async(_source("  public Detail Detail { get; set; } = new();", """
      public sealed class Detail {
        public Tag[] Tags { get; set; } = [];
      }
      """));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("'Detail.Tags'");
  }

  /// <summary>Every perspective shape names its model the same way: a global perspective's model is reported too.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AGlobalPerspectivesModel_IsReportedAsync() {
    var source = _source("  public Tag[] Tags { get; set; } = [];").Replace(
      "public class LedgerProjection : IPerspectiveFor<LedgerModel, Noted> {",
      "public class LedgerProjection : IGlobalPerspectiveFor<LedgerModel, Guid, Noted> {\n  public Guid GetPartitionKey(Noted @event) => @event.Id;",
      StringComparison.Ordinal);

    await Assert.That(await _whiz200Async(source)).Count().IsEqualTo(1);
  }

  /// <summary>A model two perspectives project is reported once per property, not once per perspective.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AModelTwoPerspectivesProject_IsReportedOnceAsync() {
    var source = _source("  public Tag[] Tags { get; set; } = [];") + """

      public class SecondLedgerProjection : IPerspectiveFor<LedgerModel, Noted> {
        public LedgerModel Apply(LedgerModel currentData, Noted eventData) => currentData;
      }
      """;

    await Assert.That(await _whiz200Async(source)).Count().IsEqualTo(1);
  }

  // ========================================
  // Not reported
  // ========================================

  /// <summary>An array of primitives is a mapped value that reads back, so it keeps the model mapped and is not reported.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("string[]")]
  [Arguments("Guid[]")]
  [Arguments("int[]")]
  [Arguments("byte[]")]
  [Arguments("float[]")]
  [Arguments("DayOfWeek[]")]
  [Arguments("int?[]")]
  public async Task AnArrayOfPrimitives_IsNotReportedAsync(string type) {
    await Assert.That(await _whiz200Async(_source($"  public {type} Values {{ get; set; }} = [];"))).IsEmpty();
  }

  /// <summary>The collection shapes the mapped path fills keep the document mapped, so they are not reported.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("List<Tag>")]
  [Arguments("IList<Tag>")]
  [Arguments("ImmutableList<Tag>")]
  public async Task ACollectionTheMappedPathFills_IsNotReportedAsync(string type) {
    await Assert.That(await _whiz200Async(_source($"  public {type} Tags {{ get; set; }} = null!;"))).IsEmpty();
  }

  /// <summary>
  /// A type no perspective projects is not a perspective model, whatever its name or attributes: the name ending in
  /// <c>Model</c> and a <c>[StreamId]</c> property used to be enough to be warned.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ATypeNoPerspectiveProjects_IsNotReported_WhateverItsNameAsync() {
    var source = _source("  public int Count { get; set; }", """
      public class OrderModel {
        [StreamId] public Guid Id { get; set; }
        public Tag[] Tags { get; set; } = [];
      }
      """);

    await Assert.That(await _whiz200Async(source)).IsEmpty();
  }

  /// <summary>
  /// The opaque form is the Entity Framework driver's: a project that does not reference it (one using the Dapper
  /// driver, which serializes every model whole) is not warned about a storage choice it never makes.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AProjectWithoutTheEntityFrameworkDriver_IsNotReportedAsync() {
    await Assert.That(await _whiz200Async(_source("  public Tag[] Tags { get; set; } = [];"), efCore: false)).IsEmpty();
  }

  // ========================================
  // Turned off
  // ========================================

  /// <summary>The build property turns the warning off for a whole project, in any casing.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("false")]
  [Arguments("FALSE")]
  [Arguments(" False ")]
  public async Task TheBuildProperty_TurnsTheWarningOffAsync(string value) {
    var reported = await _whiz200Async(
      _source("  public Tag[] Tags { get; set; } = [];"), properties: new Dictionary<string, string> { [SWITCH_PROPERTY] = value });

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>Any other value, and a project that never sets the property, leave the warning on: on is the default.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("true")]
  [Arguments("")]
  [Arguments(null)]
  public async Task AnyOtherValueOrNoValue_LeavesTheWarningOnAsync(string? value) {
    var properties = value is null ? null : new Dictionary<string, string> { [SWITCH_PROPERTY] = value };

    await Assert.That(await _whiz200Async(_source("  public Tag[] Tags { get; set; } = [];"), properties: properties))
      .Count().IsEqualTo(1);
  }

  /// <summary>The package declares the property compiler-visible, or a project's setting would never reach the analyzer.</summary>
  [Test]
  public async Task ThePackage_DeclaresTheBuildPropertyAsync() {
    await Assert.That(MSBuildGlobalOptions.Declared).Contains(SWITCH_PROPERTY);
  }

  /// <summary>A pragma around the property suppresses the warning for that property alone.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task APragma_SuppressesItForThatPropertyAsync() {
    var reported = await _whiz200Async(_source("""
      #pragma warning disable WHIZ200 // The model is read whole; nothing in it is filtered.
        public Tag[] Tags { get; set; } = [];
      #pragma warning restore WHIZ200
        public Tag[] Others { get; set; } = [];
      """));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("Others");
  }

  /// <summary>A SuppressMessage on the property suppresses the warning for that property alone.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ASuppressMessageOnTheProperty_SuppressesItForThatPropertyAsync() {
    var reported = await _whiz200Async(_source("""
        [SuppressMessage("Whizbang.ModelValidation", "WHIZ200:Array of records stores the model opaquely",
          Justification = "The model is read whole; nothing in it is filtered.")]
        public Tag[] Tags { get; set; } = [];
        public Tag[] Others { get; set; } = [];
      """));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("Others");
  }

  /// <summary>An array whose element is itself an array is stored opaquely as well, so it is reported.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnArrayOfArrays_IsReportedAsync() {
    await Assert.That(await _whiz200Async(_source("  public int[][] Grid { get; set; } = [];"))).Count().IsEqualTo(1);
  }

  /// <summary>A member whose type is not a named type (dynamic) is not walked into and is not reported.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AMemberThatIsNotANamedType_IsNotReportedAsync() {
    await Assert.That(await _whiz200Async(_source("  public dynamic? Extra { get; set; }"))).IsEmpty();
  }

  /// <summary>
  /// A model of another project has no source in this compilation to report at (here an IDE's project reference, whose
  /// source belongs to another compilation), so the warning is reported at the perspective that projects it, still
  /// naming the property, and the analyzer does not fail trying to report outside its compilation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AModelFromAnotherAssembly_IsReportedAtThePerspectiveAsync() {
    var models = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("""
      using System;
      namespace Models;
      public sealed record Tag(Guid TagId, string Label);
      public class LedgerModel {
        public Tag[] Tags { get; set; } = [];
      }
      """).WithAssemblyName("Models");
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("""
        using System;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;
        using Models;
        namespace TestApp;
        public record Noted([property: StreamId] Guid Id) : IEvent;
        public class LedgerProjection : IPerspectiveFor<LedgerModel, Noted> {
          public LedgerModel Apply(LedgerModel currentData, Noted eventData) => currentData;
        }
        """)
      .AddReferences(models.ToMetadataReference(), CSharpCompilation.Create("Whizbang.Data.EFCore.Postgres").ToMetadataReference());

    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
    await Assert.That(errors).IsEmpty().Because("the perspective has to bind to the referenced model");

    var all = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(
      compilation, globalOptions: MSBuildGlobalOptions.For());
    var reported = all.Where(d => d.Id == "WHIZ200").ToList();

    await Assert.That(all.Where(d => d.Id == "AD0001")).IsEmpty().Because("an analyzer failure would hide the warning");
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("LedgerProjection");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).Contains("'Tags'");
  }

  /// <summary>
  /// An interface that extends a perspective interface projects nothing itself, so its model is not reported through
  /// it; the type that implements it is the perspective.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnInterfaceExtendingAPerspectiveInterface_IsNotAPerspectiveAsync() {
    var source = _source("  public Tag[] Tags { get; set; } = [];").Replace(
      "public class LedgerProjection : IPerspectiveFor<LedgerModel, Noted> {\n  public LedgerModel Apply(LedgerModel currentData, Noted eventData) => currentData;\n}",
      "public interface ILedgerProjection : IPerspectiveFor<LedgerModel, Noted> { }",
      StringComparison.Ordinal);

    await Assert.That(source).Contains("public interface ILedgerProjection")
      .Because("the fixture must hold the interface and no implementing type, or the silence proves nothing");
    await Assert.That(await _whiz200Async(source)).IsEmpty();
  }

  /// <summary>A struct can be a perspective too (a record struct included), so its model is reported.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("public struct LedgerProjection")]
  [Arguments("public readonly record struct LedgerProjection")]
  public async Task AStructPerspectivesModel_IsReportedAsync(string declaration) {
    var source = _source("  public Tag[] Tags { get; set; } = [];").Replace(
      "public class LedgerProjection", declaration, StringComparison.Ordinal);

    await Assert.That(source).Contains(declaration);
    await Assert.That(await _whiz200Async(source)).Count().IsEqualTo(1);
  }

  /// <summary>
  /// A model compiled into a referenced assembly (a metadata reference, as a build of a project reference is) has no
  /// source location at all, so the warning is reported at the perspective.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AModelFromAReferencedAssembly_IsReportedAtThePerspectiveAsync() {
    var models = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("""
      using System;
      namespace Models;
      public sealed record Tag(Guid TagId, string Label);
      public class LedgerModel {
        public Tag[] Tags { get; set; } = [];
      }
      """).WithAssemblyName("Models").WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    await using var image = new MemoryStream();
    var emitted = models.Emit(image);
    await Assert.That(emitted.Success).IsTrue().Because("the model assembly has to compile to be referenced");
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("""
        using System;
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;
        using Models;
        namespace TestApp;
        public record Noted([property: StreamId] Guid Id) : IEvent;
        public class LedgerProjection : IPerspectiveFor<LedgerModel, Noted> {
          public LedgerModel Apply(LedgerModel currentData, Noted eventData) => currentData;
        }
        """)
      .AddReferences(MetadataReference.CreateFromImage(image.ToArray()),
        CSharpCompilation.Create("Whizbang.Data.EFCore.Postgres").ToMetadataReference());

    var all = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(
      compilation, globalOptions: MSBuildGlobalOptions.For());
    var reported = all.Where(d => d.Id == "WHIZ200").ToList();

    await Assert.That(all.Where(d => d.Id == "AD0001")).IsEmpty();
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("LedgerProjection");
  }

  /// <summary>
  /// A member the mapped document leaves out (static, non-public, or marked [JsonIgnore] or [NotMapped]) does not make
  /// the model opaque, so an array of records there is not reported.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("public static Tag[] Shared { get; set; } = [];")]
  [Arguments("internal Tag[] Internal { get; set; } = [];")]
  [Arguments("[System.Text.Json.Serialization.JsonIgnore] public Tag[] Ignored { get; set; } = [];")]
  public async Task AnArrayOfRecordsTheDocumentLeavesOut_IsNotReportedAsync(string member) {
    await Assert.That(await _whiz200Async(_source($"  {member}"))).IsEmpty();
  }
}
