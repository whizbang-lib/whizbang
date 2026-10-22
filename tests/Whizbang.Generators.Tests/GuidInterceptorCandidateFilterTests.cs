// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The GUID interceptor's syntax predicate must keep ordinary member-access calls away from the semantic
/// transform. Every call that reaches the transform costs a symbol lookup, and a consumer has tens of
/// thousands of member-access calls and a handful of GUID calls, so a predicate that admits every
/// member-access call makes the generator the most expensive step of the build even with interception off.
/// </summary>
/// <remarks>
/// The predicate is tested directly rather than through the generator's tracked steps: Roslyn does not
/// record a transform result equal to the default value, and the transform answers <c>(null, null)</c> for
/// every call it does not intercept, so counting tracked outputs reads the same with or without the filter.
/// </remarks>
/// <docs>operations/performance/build-cost</docs>
[Category("Generators")]
[Category("Interceptors")]
[Category("BuildCost")]
public class GuidInterceptorCandidateFilterTests {
  private const string CANDIDATES_STEP = "GuidCallCandidates";
  private const int ORDINARY_CALLS = 50;

  [Test]
  public async Task IsGuidCallCandidate_OrdinaryMemberAccessCalls_AreRejectedAsync() {
    var root = await CSharpSyntaxTree.ParseText(_sourceWithOrdinaryCalls()).GetRootAsync();

    var memberAccessCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Count(i => i.Expression is MemberAccessExpressionSyntax);
    var candidates = root.DescendantNodes().Count(GuidInterceptorGenerator.IsGuidCallCandidate);

    await Assert.That(memberAccessCalls).IsEqualTo(ORDINARY_CALLS + 1);
    await Assert.That(candidates).IsEqualTo(1)
        .Because($"only the one Guid.NewGuid() call among {ORDINARY_CALLS} ordinary calls should reach the transform");
  }

  [Test]
  [Arguments("System.Guid.NewGuid()")]
  [Arguments("Guid.CreateVersion7()")]
  [Arguments("CombGuidIdGeneration.NewGuid()")]
  [Arguments("Uuid.NewDatabaseFriendly(Database.PostgreSql)")]
  [Arguments("Uuid.NewSequential()")]
  [Arguments("Uuid7.NewUuid7()")]
  public async Task IsGuidCallCandidate_EveryInterceptableMethodName_IsAcceptedAsync(string call) {
    // The filter names the methods the transform can intercept; one it left out would silently stop
    // being intercepted. The predicate reads names alone, so the call need not resolve.
    var invocation = SyntaxFactory.ParseExpression(call);

    await Assert.That(GuidInterceptorGenerator.IsGuidCallCandidate(invocation)).IsTrue();
  }

  [Test]
  [Arguments("NewGuid()")]
  [Arguments("Guid.Empty")]
  [Arguments("s.ToString()")]
  public async Task IsGuidCallCandidate_NotAMemberAccessCallToAnInterceptableName_IsRejectedAsync(string expression) {
    var node = SyntaxFactory.ParseExpression(expression);

    await Assert.That(GuidInterceptorGenerator.IsGuidCallCandidate(node)).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_CandidateStep_IsTrackedByNameAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation(_sourceWithOrdinaryCalls());

    GeneratorDriver driver = CSharpGeneratorDriver.Create(
        [new GuidInterceptorGenerator().AsSourceGenerator()],
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
    driver = driver.RunGenerators(compilation);

    var steps = driver.GetRunResult().Results.Single().TrackedSteps;
    var hasStep = steps.TryGetValue(CANDIDATES_STEP, out var candidates);
    await Assert.That(hasStep).IsTrue()
        .Because($"the syntax provider step must be tracked as {CANDIDATES_STEP}");
    await Assert.That(candidates.SelectMany(step => step.Outputs).Count()).IsEqualTo(1);
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
