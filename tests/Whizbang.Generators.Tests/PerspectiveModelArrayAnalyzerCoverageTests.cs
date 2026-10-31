// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A type whose name ends in <c>Model</c> is not a perspective model unless a perspective projects it (#1910): the old
/// naming heuristic is gone, and an ordinary DTO or view model with an array is never warned.
/// </summary>
/// <tests>Whizbang.Generators.Tests/PerspectiveModelArrayAnalyzerTests.cs</tests>
[Category("Analyzers")]
public class PerspectiveModelArrayAnalyzerCoverageTests {
  /// <summary>
  /// "...Model" is a common suffix for ordinary DTOs and view models that have nothing to do with perspectives. None of
  /// them is a perspective model unless a perspective projects it, so an array in one is never reported.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ModelSuffixedClassWithNoStreamIdProperty_NoWarningAsync() {
    // No perspective projects OrderSummaryModel, whatever its name.
    const string source = """
        using System;

        namespace TestApp {
          public class OrderSummaryModel {
            [Obsolete("legacy")]
            public string Description { get; set; }

            public string[] Tags { get; set; }
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelArrayAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ200")).IsEmpty()
      .Because("no perspective projects OrderSummaryModel, so it is not a perspective model whatever its name");
  }
}
