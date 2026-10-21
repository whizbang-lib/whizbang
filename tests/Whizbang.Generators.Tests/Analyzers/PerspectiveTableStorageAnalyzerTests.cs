// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for <see cref="PerspectiveTableStorageAnalyzer"/> (#1592): a <c>[PerspectiveTableStorage]</c> value outside the
/// range PostgreSQL accepts is reported where it is declared (WHIZ804), rather than failing the schema pass on the
/// server at startup.
/// </summary>
/// <docs>operations/diagnostics/whiz804</docs>
/// <code-under-test>src/Whizbang.Generators/Analyzers/PerspectiveTableStorageAnalyzer.cs</code-under-test>
public class PerspectiveTableStorageAnalyzerTests {
  private static string _model(string arguments) => $$"""
    using Whizbang.Core.Perspectives;
    namespace App;
    [PerspectiveTableStorage({{arguments}})]
    public record Doc { public string Name { get; init; } = ""; }
    """;

  [Test]
  public async Task ValuesInRange_AreNotReportedAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveTableStorageAnalyzer>(_model(
      "DataCompression = ColumnCompression.Lz4, FillFactor = 10, ToastTupleTarget = 8160, AutovacuumVacuumThreshold = 0, "
      + "AutovacuumVacuumScaleFactor = 100, AutovacuumInsertScaleFactor = 0, ToastAutovacuumVacuumScaleFactor = 0.01"));

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ804")).IsEqualTo(0);
  }

  [Test]
  public async Task NegativeValues_DeclareNothing_AndAreNotReportedAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveTableStorageAnalyzer>(_model(
      "FillFactor = -1, ToastTupleTarget = 0, AutovacuumVacuumScaleFactor = -1, AutovacuumInsertThreshold = -5"));

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ804")).IsEqualTo(0);
  }

  [Test]
  public async Task AModelWithoutTheAttribute_IsNotReportedAsync() {
    const string source = """
      namespace App;
      public record Doc { public string Name { get; init; } = ""; }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveTableStorageAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ804")).IsEqualTo(0);
  }

  [Test]
  [Arguments("FillFactor = 9", "FillFactor", "9", "10", "100")]
  [Arguments("FillFactor = 101", "FillFactor", "101", "10", "100")]
  [Arguments("ToastTupleTarget = 64", "ToastTupleTarget", "64", "128", "8160")]
  [Arguments("AutovacuumVacuumScaleFactor = 150", "AutovacuumVacuumScaleFactor", "150", "0", "100")]
  [Arguments("AutovacuumInsertScaleFactor = 100.5", "AutovacuumInsertScaleFactor", "100.5", "0", "100")]
  [Arguments("ToastAutovacuumVacuumScaleFactor = 200", "ToastAutovacuumVacuumScaleFactor", "200", "0", "100")]
  public async Task AValueOutOfRange_ReportsWhiz804Async(string arguments, string property, string value, string minimum, string maximum) {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveTableStorageAnalyzer>(_model(arguments));

    var whiz804 = diagnostics.Single(d => d.Id == "WHIZ804");
    var message = whiz804.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("'Doc'");
    await Assert.That(message).Contains($"{property} = {value}");
    await Assert.That(message).Contains($"{minimum} to {maximum}");
    await Assert.That(whiz804.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
  }

  [Test]
  public async Task EachValueOutOfRange_IsReportedOnItsOwnAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveTableStorageAnalyzer>(_model(
      "FillFactor = 5, AutovacuumVacuumScaleFactor = 101"));

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ804")).IsEqualTo(2);
  }
}
