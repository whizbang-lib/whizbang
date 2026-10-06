// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ810 recognizes each dictionary shape a model can declare, both interfaces included,
/// and does not mistake another two-argument generic, such as a key/value pair, for one.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/PerspectiveModelDictionaryAnalyzer.cs</code-under-test>
public class PerspectiveModelDictionaryAnalyzerBranchTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task ReadOnlyDictionary_IsReported_AndAKeyValuePairIsNotAsync() {
    const string source = """
      using System;
      using System.Collections.Generic;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record ScoreEvent : IEvent;

      public class ScoreModel {
        public Guid Id { get; set; }
        public IReadOnlyDictionary<string, int> Totals { get; set; } = new Dictionary<string, int>();
        public IDictionary<string, int> Tallies { get; set; } = new Dictionary<string, int>();
        public KeyValuePair<string, int> Best { get; set; }
      }

      public class ScorePerspective : IPerspectiveFor<ScoreModel, ScoreEvent> {
        public ScoreModel Apply(ScoreModel currentData, ScoreEvent @event) => currentData;
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelDictionaryAnalyzer>(source);
    var whiz810 = diagnostics.Where(d => d.Id == "WHIZ810").Select(d => d.GetMessage(CultureInfo.InvariantCulture)).ToList();

    await Assert.That(whiz810).IsNotEmpty();
    await Assert.That(whiz810).Contains(m => m.Contains("Totals", StringComparison.Ordinal));
    await Assert.That(whiz810).Contains(m => m.Contains("Tallies", StringComparison.Ordinal));
    await Assert.That(whiz810.All(m => m.Contains("Totals", StringComparison.Ordinal) || m.Contains("Tallies", StringComparison.Ordinal))).IsTrue()
      .Because("only the dictionary interfaces are reported; the key/value pair is not a dictionary");
  }
}
