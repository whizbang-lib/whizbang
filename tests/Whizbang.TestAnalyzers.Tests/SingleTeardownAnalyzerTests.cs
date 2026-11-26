// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Whizbang.TestAnalyzers.Tests;

/// <summary>
/// WHIZ609 (#2468): TUnit runs a fixture's <c>[After(Test)]</c> hooks and then disposes the instance when it is
/// <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>, so a fixture that is both tears down twice. The build
/// rejects it in every test project, where #1954 guarded one project with a test of its own.
/// </summary>
public class SingleTeardownAnalyzerTests {
  private const string USINGS = """
    using System;
    using System.Threading.Tasks;
    using TUnit.Core;
    using static TUnit.Core.HookType;

    """;

  private static Task<ImmutableArray<Diagnostic>> _analyzeAsync(string source) =>
    AnalyzerHarness.AnalyzeAsync(new SingleTeardownAnalyzer(), USINGS + source);

  private static string _message(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

  [Test]
  public async Task AsyncDisposableFixture_WithATestTeardownHook_ReportsWhiz609Async() {
    var diagnostics = await _analyzeAsync("""
      public class OrdersTests : IAsyncDisposable {
        [Test] public void Run() { }
        [After(Test)] public Task TeardownAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync() => await TeardownAsync();
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ609");
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(_message(diagnostics[0])).Contains("'OrdersTests'").And.Contains("IAsyncDisposable");
  }

  [Test]
  public async Task DisposableFixture_WithATestTeardownHook_ReportsWhiz609Async() {
    var diagnostics = await _analyzeAsync("""
      public sealed class OrdersTests : IDisposable {
        [Test] public void Run() { }
        [After(Test)] public void Cleanup() { }
        public void Dispose() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(_message(diagnostics[0])).Contains("IDisposable");
  }

  [Test]
  public async Task AHookThatIsTheDisposeMethodItself_IsReportedAsync() {
    // TUnit calls it as the hook and again as the dispose.
    var diagnostics = await _analyzeAsync("""
      public class OrdersTests : IAsyncDisposable {
        [Test] public void Run() { }
        [After(Test)] public ValueTask DisposeAsync() => default;
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  public async Task AFixtureThatIsBoth_NamesBothInterfacesAsync() {
    var diagnostics = await _analyzeAsync("""
      public class OrdersTests : IDisposable, IAsyncDisposable {
        [Test] public void Run() { }
        [After(Test)] public void Cleanup() { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(_message(diagnostics[0])).Contains("IAsyncDisposable and IDisposable");
  }

  [Test]
  public async Task AHookAlone_IsTheOneTeardown_AndReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      public class OrdersTests {
        [Test] public void Run() { }
        [After(Test)] public Task TeardownAsync() => Task.CompletedTask;
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task ADisposableFixtureWithoutAHook_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      public class OrdersTests : IAsyncDisposable {
        [Test] public void Run() { }
        public ValueTask DisposeAsync() => default;
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task OtherHooks_OnADisposableFixture_ReportNothingAsync() {
    // [Before(Test)] is setup and [After(Class)] runs once per class, not per instance: neither is the per-test teardown.
    var diagnostics = await _analyzeAsync("""
      public class OrdersTests : IDisposable {
        [Test] public void Run() { }
        [Before(Test)] public void Setup() { }
        [After(Class)] public static void ClassTeardown() { }
        public void Dispose() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task ALookAlikeAfterAttribute_ReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      namespace Elsewhere {
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class AfterAttribute(HookType hook) : Attribute { public HookType Hook { get; } = hook; }
      }

      public class OrdersTests : IDisposable {
        [Test] public void Run() { }
        [Elsewhere.After(HookType.Test)] public void Cleanup() { }
        public void Dispose() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task AHookInTheBaseClass_AndTheInterfaceInTheDerived_ReportsTheDerivedAsync() {
    var diagnostics = await _analyzeAsync("""
      public abstract class DatabaseTestBase {
        [After(Test)] public Task TeardownAsync() => Task.CompletedTask;
      }

      public sealed class OrdersTests : DatabaseTestBase, IDisposable {
        [Test] public void Run() { }
        public void Dispose() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(_message(diagnostics[0])).Contains("'OrdersTests'");
  }

  [Test]
  public async Task ABaseClassThatIsBoth_IsReportedOnce_OnTheBaseAsync() {
    // The fix goes on the base; reporting each derived fixture would bury the one line that needs changing.
    var diagnostics = await _analyzeAsync("""
      public abstract class DatabaseTestBase : IAsyncDisposable {
        [After(Test)] public Task TeardownAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync() => await TeardownAsync();
      }

      public sealed class OrdersTests : DatabaseTestBase {
        [Test] public void Run() { }
      }

      public sealed class InvoicesTests : DatabaseTestBase {
        [Test] public void Run() { }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(_message(diagnostics[0])).Contains("'DatabaseTestBase'");
  }

  [Test]
  public async Task ABaseClassThatIsBoth_FromAnotherAssembly_IsReportedOnTheDerivedAsync() {
    // The base's own project may not load this analyzer, so the fixture this project declares carries the report.
    var library = AnalyzerHarness.Compile([USINGS + """
      public abstract class DatabaseTestBase : IAsyncDisposable {
        [After(Test)] public Task TeardownAsync() => Task.CompletedTask;
        public async ValueTask DisposeAsync() => await TeardownAsync();
      }
      """], "SharedFixtures", []);
    var subject = AnalyzerHarness.Compile([USINGS + """
      public sealed class OrdersTests : DatabaseTestBase {
        [Test] public void Run() { }
      }
      """], "AnalyzerHarnessSubject", [library.ToMetadataReference()]);

    var diagnostics = await AnalyzerHarness.AnalyzeAsync(
      new SingleTeardownAnalyzer(), subject, ImmutableDictionary<string, string>.Empty);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(_message(diagnostics[0])).Contains("'OrdersTests'");
  }

  [Test]
  public async Task AStruct_IsNoFixture_AndReportsNothingAsync() {
    var diagnostics = await _analyzeAsync("""
      public struct Holder : IDisposable {
        [After(Test)] public void Cleanup() { }
        public void Dispose() { }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task WithoutTUnit_ReportsNothingAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new SingleTeardownAnalyzer(), """
      using System;
      public sealed class Holder : IDisposable {
        public void Dispose() { }
      }
      """, referenceTUnit: false);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task Diagnostic_IsLocatedOnTheClassIdentifierAsync() {
    var diagnostics = await _analyzeAsync("""
      public class OrdersTests : IDisposable {
        [Test] public void Run() { }
        [After(Test)] public void Cleanup() { }
        public void Dispose() { }
      }
      """);

    var location = diagnostics.Single().Location;
    var text = (await location.SourceTree!.GetTextAsync()).ToString(location.SourceSpan);
    await Assert.That(text).IsEqualTo("OrdersTests");
  }
}
