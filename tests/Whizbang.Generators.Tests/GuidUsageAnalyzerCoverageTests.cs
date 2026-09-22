using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused test for <see cref="GuidUsageAnalyzer"/>, complementing
/// <c>tests/Whizbang.Generators.Tests/GuidUsageAnalyzerTests.cs</c>. Every existing test source
/// only ever calls qualified member-access invocations (<c>Guid.NewGuid()</c>, <c>Guid.Parse(x)</c>,
/// <c>TrackedGuid.NewMedo()</c>, ...), so <c>_analyzeInvocation</c>'s "not a
/// <c>MemberAccessExpressionSyntax</c>" guard has never run — every real-world compilation also
/// contains unqualified invocations (local method calls, delegate invocations, <c>nameof</c>), so
/// this is not a dead branch.
/// </summary>
[Category("Analyzers")]
public class GuidUsageAnalyzerCoverageTests {

  /// <summary>
  /// The analyzer registers for every <c>InvocationExpressionSyntax</c> in a compilation, not just
  /// ones shaped like <c>Type.Method()</c>. If the "not a member access" guard regressed, calling
  /// an ordinary unqualified method — something present in essentially every C# file — would throw
  /// an <c>InvalidCastException</c> trying to read <c>memberAccess.Name</c> off a node that isn't a
  /// <c>MemberAccessExpressionSyntax</c>, crashing analysis of the entire file rather than just
  /// skipping the node.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_UnqualifiedInvocation_DoesNotCrashAndIsNotReportedAsync() {
    // Arrange — Validate() has no receiver, so its Expression is an IdentifierNameSyntax rather
    // than a MemberAccessExpressionSyntax.
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public Guid CreateId() {
                Validate();
                return Guid.NewGuid();
              }

              private void Validate() { }
            }
            """;

    // Act
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GuidUsageAnalyzer>(source);

    // Assert — analysis completes (no thrown exception surfaces as a test failure) and the
    // unqualified call itself produces no diagnostic; only the qualified Guid.NewGuid() call does.
    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ055")).Count().IsEqualTo(1)
      .Because("the unqualified Validate() call must be skipped by the member-access guard, leaving only the real Guid.NewGuid() usage reported");
  }
}
