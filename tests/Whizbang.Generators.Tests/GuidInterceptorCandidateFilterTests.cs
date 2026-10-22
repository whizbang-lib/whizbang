// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The GUID interceptor's syntax predicate must keep ordinary member-access calls away from the semantic
/// transform. Every call that reaches the transform costs a symbol lookup, and a consumer has tens of
/// thousands of member-access calls and a handful of GUID calls, so a predicate that admits every
/// member-access call makes the generator the most expensive step of the build even with interception off.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
[Category("Generators")]
[Category("Interceptors")]
[Category("BuildCost")]
public class GuidInterceptorCandidateFilterTests {
  private const string CANDIDATES_STEP = "GuidCallCandidates";
  private const int ORDINARY_CALLS = 50;

  [Test]
  [RequiresAssemblyFiles]
  public async Task Predicate_OrdinaryMemberAccessCalls_NeverReachTheTransformAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation(_sourceWithOrdinaryCalls());

    GeneratorDriver driver = CSharpGeneratorDriver.Create(
        [new GuidInterceptorGenerator().AsSourceGenerator()],
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
    driver = driver.RunGenerators(compilation);

    var steps = driver.GetRunResult().Results.Single().TrackedSteps;
    var hasStep = steps.TryGetValue(CANDIDATES_STEP, out var candidates);
    await Assert.That(hasStep).IsTrue()
        .Because($"the syntax provider step must be tracked as {CANDIDATES_STEP}");

    var reachedTransform = candidates.SelectMany(step => step.Outputs).Count();
    await Assert.That(reachedTransform).IsEqualTo(1)
        .Because($"only the one Guid.NewGuid() call among {ORDINARY_CALLS} ordinary calls should reach the transform");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Predicate_EveryInterceptableMethodName_ReachesTheTransformAsync() {
    // The filter names the methods the transform can intercept; one it left out would silently stop
    // being intercepted. Names alone decide the predicate, so look-alike types are enough here.
    const string source = """
        namespace Lookalike;

        public static class Ids {
          public static void Make() {
            System.Guid.NewGuid();
            System.Guid.CreateVersion7();
            Uuid.NewDatabaseFriendly();
            Uuid.NewSequential();
            Uuid.NewUuid7();
          }
        }

        public static class Uuid {
          public static void NewDatabaseFriendly() { }
          public static void NewSequential() { }
          public static void NewUuid7() { }
        }
        """;
    var compilation = GeneratorTestHelper.CreateCompilation(source);

    GeneratorDriver driver = CSharpGeneratorDriver.Create(
        [new GuidInterceptorGenerator().AsSourceGenerator()],
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
    driver = driver.RunGenerators(compilation);

    var reachedTransform = driver.GetRunResult().Results.Single().TrackedSteps[CANDIDATES_STEP]
        .SelectMany(step => step.Outputs).Count();
    await Assert.That(reachedTransform).IsEqualTo(5);
  }

  private static string _sourceWithOrdinaryCalls() {
    var sb = new StringBuilder();
    sb.AppendLine("using System;");
    sb.AppendLine();
    sb.AppendLine("namespace TestApp;");
    sb.AppendLine();
    sb.AppendLine("public class MyService {");
    sb.AppendLine("  public Guid Work(string s, object x, object y) {");
    for (var i = 0; i < ORDINARY_CALLS / 5; i++) {
      sb.AppendLine("    _ = s.ToString();");
      sb.AppendLine("    _ = x.Equals(y);");
      sb.AppendLine("    _ = s.Trim();");
      sb.AppendLine("    _ = s.Contains(\"a\");");
      sb.AppendLine("    _ = x.GetHashCode();");
    }
    sb.AppendLine("    return Guid.NewGuid();");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    return sb.ToString();
  }
}
