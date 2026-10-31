// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// An <c>[OverwriteOnly]</c> perspective declares that no Apply reads its current model (#1869). The marker is the
/// developer's, never inferred; an Apply that contradicts it is reported, because its rewind applies only the last event.
/// </summary>
/// <tests>Whizbang.Generators/PerspectiveCurrentModelAnalyzer.cs</tests>
[Category("Analyzers")]
public class OverwriteOnlyAnalyzerTests {
  private const string PREAMBLE = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record Grants {
        [StreamId] public Guid UserId { get; init; }
        public string Set { get; init; } = "";
        public int Changes { get; init; }
      }

      public record GrantsSet : IEvent {
        [StreamId] public Guid UserId { get; init; }
        public string Set { get; init; } = "";
      }

      public record GrantsCleared : IEvent {
        [StreamId] public Guid UserId { get; init; }
      }

      """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnOverwriteOnlyApplyThatReadsItsCurrentModel_IsWarnedAsync() {
    const string source = PREAMBLE + """
        [OverwriteOnly]
        public class GrantsPerspective : IPerspectiveFor<Grants, GrantsSet, GrantsCleared> {
          public Grants Apply(Grants current, GrantsSet @event) =>
            current with { Set = @event.Set, Changes = current.Changes + 1 };

          public Grants Apply(Grants current, GrantsCleared @event) => Keep(current);

          private static Grants Keep(Grants grants) => grants;
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    var whiz108 = diagnostics.Where(d => d.Id == "WHIZ108").ToArray();
    await Assert.That(whiz108.Length).IsEqualTo(2)
      .Because("each Apply that reads its model is reported once, however often it reads it");
    await Assert.That(whiz108[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = whiz108[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("GrantsPerspective");
    await Assert.That(message).Contains("current");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnOverwriteOnlyApplyThatIgnoresItsCurrentModel_IsNotWarnedAsync() {
    const string source = PREAMBLE + """
        [OverwriteOnly]
        public class GrantsPerspective : IPerspectiveFor<Grants, GrantsSet, GrantsCleared> {
          public Grants Apply(Grants current, GrantsSet @event) => new() { UserId = @event.UserId, Set = @event.Set };

          public Grants Apply(Grants current, GrantsCleared @event) =>
            new() { UserId = @event.UserId, Set = nameof(current) };
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ108")).IsFalse()
      .Because("an Apply that builds the model from the event alone, naming the parameter at most, keeps the marker's promise");
  }

  /// <summary>
  /// Passing the model to a method reads it, whatever form the call takes; naming it in <c>nameof</c> does not.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnOverwriteOnlyApplyThatPassesItsModelToAMethod_IsWarned_ButNameofIsNotAReadAsync() {
    const string source = PREAMBLE + """
        public static class Keeper {
          public static Grants Keep(Grants grants) => grants;
        }

        [OverwriteOnly]
        public class GrantsPerspective : IPerspectiveFor<Grants, GrantsSet, GrantsCleared> {
          public Grants Apply(Grants current, GrantsSet @event) => Keeper.Keep(current);

          public Grants Apply(Grants current, GrantsCleared @event) =>
            new() { UserId = @event.UserId, Set = nameof(current) };
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    var whiz108 = diagnostics.Where(d => d.Id == "WHIZ108").ToArray();
    await Assert.That(whiz108.Length).IsEqualTo(1)
      .Because("the model passed through a member call is read; the one only named in nameof is not");
    await Assert.That(whiz108[0].GetMessage(CultureInfo.InvariantCulture)).Contains("current");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnUnmarkedApplyThatReadsItsCurrentModel_IsNotWarnedAsync() {
    const string source = PREAMBLE + """
        public class GrantsPerspective : IPerspectiveFor<Grants, GrantsSet> {
          public Grants Apply(Grants current, GrantsSet @event) => current with { Set = @event.Set };
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveCurrentModelAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ108")).IsFalse()
      .Because("folding onto the current model is the default; only the marker promises otherwise");
  }
}
