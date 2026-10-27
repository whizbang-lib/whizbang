// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;

namespace Whizbang.TestAnalyzers.Tests;

/// <summary>
/// WHZ0004: <c>IsNotNull()</c> on a <c>Nullable&lt;T&gt;</c> subject passes when the value is null under
/// the TUnit this repository pins, so the rule reports every such call.
/// </summary>
public class NullableIsNotNullAnalyzerTests {
  private const string USINGS = """
    using System;
    using System.Threading.Tasks;
    using TUnit.Assertions;
    using TUnit.Assertions.Extensions;

    """;

  [Test]
  public async Task IsNotNull_OnNullableInt_ReportsWhz0004Async() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync(int? count) {
          await Assert.That(count).IsNotNull();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHZ0004");
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
  }

  [Test]
  public async Task IsNotNull_OnNullableStructMember_NamesTheSubjectAndItsUnderlyingTypeAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public sealed record Sample(TimeSpan? OldestAge);
      public static class Subject {
        public static async Task RunAsync(Sample sample) {
          await Assert.That(sample.OldestAge).IsNotNull();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    var message = diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("sample.OldestAge");
    await Assert.That(message).Contains("TimeSpan");
  }

  [Test]
  public async Task IsNotNull_Diagnostic_IsLocatedOnTheIsNotNullNameAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync(long? value) {
          await Assert.That(value).IsNotNull();
        }
      }
      """);

    var location = diagnostics.Single().Location;
    var text = (await location.SourceTree!.GetTextAsync()).ToString(location.SourceSpan);
    await Assert.That(text).IsEqualTo("IsNotNull");
  }

  [Test]
  public async Task IsNotNull_AfterAndContinuation_OnNullableSubject_ReportsWhz0004Async() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync(int? count) {
          await Assert.That(count).IsNotEqualTo(3).And.IsNotNull();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  public async Task IsNotNull_ResultUsedAsValue_ReportsWhz0004Async() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task<int> RunAsync(int? count) {
          var value = await Assert.That(count).IsNotNull();
          return value;
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  public async Task IsNotNull_OnGenericNullableSubject_ReportsWhz0004Async() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync<T>(T? value) where T : struct {
          await Assert.That(value).IsNotNull();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  public async Task IsNotNull_OnReferenceTypeSubject_ReportsNothingAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync(string? name, object? item) {
          await Assert.That(name).IsNotNull();
          await Assert.That(item).IsNotNull();
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task IsNull_OnNullableSubject_ReportsNothingAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync(int? count) {
          await Assert.That(count).IsNull();
          await Assert.That(count.HasValue).IsTrue();
        }
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task IsNotNull_DeclaredOutsideTUnit_ReportsNothingAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), """
      public static class OwnAssertions {
        public static bool IsNotNull<T>(this T? value) where T : struct => value.HasValue;
      }
      public static class Subject {
        public static bool Run(int? count) => count.IsNotNull();
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task IsNotNull_OnAStoredAssertionSource_NamesTheSourceAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync(int? count) {
          var source = Assert.That(count);
          await source.IsNotNull();
        }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    var message = diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).StartsWith("'source' is a Nullable<int>");
  }

  [Test]
  public async Task IsNotNull_AfterConditionalAccess_ReportsOnTheMethodNameAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync(int? count) {
          await Assert.That(count)?.IsNotNull()!;
        }
      }
      """);

    var location = diagnostics.Single().Location;
    var text = (await location.SourceTree!.GetTextAsync()).ToString(location.SourceSpan);
    await Assert.That(text).IsEqualTo("IsNotNull");
    var message = diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).StartsWith("'count' is a Nullable<int>");
  }

  [Test]
  public async Task IsNotNull_CalledInStaticForm_ReportsWhz0004Async() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), USINGS + """
      public static class Subject {
        public static async Task RunAsync(int? count) {
          await AssertionExtensions.IsNotNull(Assert.That(count));
        }
      }
      """);

    var message = diagnostics.Single().GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).StartsWith("'count' is a Nullable<int>");
  }

  [Test]
  public async Task Compilation_WithoutTUnit_ReportsNothingAsync() {
    var diagnostics = await AnalyzerHarness.AnalyzeAsync(new NullableIsNotNullAnalyzer(), """
      public static class Subject {
        public static bool IsNotNull<T>(this T? value) where T : struct => value.HasValue;
        public static bool Run(int? count) => count.IsNotNull();
      }
      """, referenceTUnit: false);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task Descriptor_CarriesTheRuleAndTheReplacementAsync() {
    var descriptor = NullableIsNotNullAnalyzer.IsNotNullOnNullable;

    await Assert.That(descriptor.Id).IsEqualTo("WHZ0004");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
    await Assert.That(descriptor.Description.ToString(System.Globalization.CultureInfo.InvariantCulture)).Contains(".HasValue");
    await Assert.That(descriptor.HelpLinkUri).Contains("testing-tunit.md");
    await Assert.That(new NullableIsNotNullAnalyzer().SupportedDiagnostics).Contains(descriptor);
  }
}
