// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for <see cref="PerspectiveFieldAnalyzer"/> (#1427): <c>[VectorField]</c> on a property that is not
/// <c>float[]</c> (WHIZ801) or with non-positive dimensions (WHIZ802), and a Split model with no physical or vector
/// field (WHIZ805).
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/PerspectiveFieldAnalyzer.cs</code-under-test>
public class PerspectiveFieldAnalyzerTests {
  [Test]
  public async Task AVectorFieldOnFloatArray_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core.Perspectives;
      namespace App;
      public record Doc { [VectorField(3)] public float[] Embedding { get; init; } = []; }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFieldAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id is "WHIZ801" or "WHIZ802")).IsEqualTo(0);
  }

  [Test]
  [Arguments("double[]")]
  [Arguments("float")]
  [Arguments("System.Collections.Generic.List<float>")]
  public async Task AVectorFieldOnAnotherType_ReportsWhiz801Async(string type) {
    var source = $$"""
      using Whizbang.Core.Perspectives;
      namespace App;
      public record Doc { [VectorField(3)] public {{type}} Embedding { get; init; } = default!; }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFieldAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ801")).IsEqualTo(1);
  }

  [Test]
  [Arguments(0)]
  [Arguments(-4)]
  public async Task AVectorFieldWithNonPositiveDimensions_ReportsWhiz802Async(int dimensions) {
    var source = $$"""
      using Whizbang.Core.Perspectives;
      namespace App;
      public record Doc { [VectorField({{dimensions}})] public float[] Embedding { get; init; } = []; }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFieldAnalyzer>(source);

    var whiz802 = diagnostics.Single(d => d.Id == "WHIZ802");
    await Assert.That(whiz802.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains(dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  [Test]
  public async Task ASplitModelWithNoPhysicalField_ReportsWhiz805Async() {
    const string source = """
      using Whizbang.Core.Perspectives;
      namespace App;
      [PerspectiveStorage(FieldStorageMode.Split)]
      public record Doc { public string Name { get; init; } = ""; }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFieldAnalyzer>(source);

    var whiz805 = diagnostics.Single(d => d.Id == "WHIZ805");
    await Assert.That(whiz805.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("'Doc'");
  }

  [Test]
  [Arguments("[PhysicalField] public string Name { get; init; } = \"\";")]
  [Arguments("[VectorField(3)] public float[] Embedding { get; init; } = [];")]
  public async Task ASplitModelWithAPhysicalOrVectorField_IsNotReportedAsync(string member) {
    var source = $$"""
      using Whizbang.Core.Perspectives;
      namespace App;
      [PerspectiveStorage(FieldStorageMode.Split)]
      public record Doc { {{member}} }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFieldAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ805")).IsEqualTo(0);
  }

  [Test]
  public async Task ASplitModelWhosePhysicalFieldIsInherited_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core.Perspectives;
      namespace App;
      public record DocBase { [PhysicalField] public string Name { get; init; } = ""; }
      [PerspectiveStorage(FieldStorageMode.Split)]
      public record Doc : DocBase;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFieldAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ805")).IsEqualTo(0);
  }

  [Test]
  public async Task AJsonOnlyModelWithNoPhysicalField_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core.Perspectives;
      namespace App;
      [PerspectiveStorage(FieldStorageMode.JsonOnly)]
      public record Doc { public string Name { get; init; } = ""; }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveFieldAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ805")).IsEqualTo(0);
  }
}
