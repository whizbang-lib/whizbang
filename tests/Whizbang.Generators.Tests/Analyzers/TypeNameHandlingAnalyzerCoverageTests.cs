using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Branch coverage for the recognizers behind the four type-name rules. The rule-level tests in
/// <see cref="TypeNameHandlingAnalyzerTests"/> prove one positive and one negative per rule; these
/// drive the shapes underneath that those fixtures never reach, so a recognizer cannot quietly
/// change behavior. An analyzer with untested branches is worse than an untested helper: it runs
/// inside every consumer's build, and a misfire is a diagnostic on code that is actually fine.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/TypeNameHandlingAnalyzer.cs</code-under-test>
/// <docs>operations/diagnostics/whiz160</docs>
public class TypeNameHandlingAnalyzerCoverageTests {
  private const string PRELUDE = """
    using System;
    namespace Whizbang.Core {
      public static class TypeNameFormatter {
        public static string Format(Type t) => t.FullName + ", " + t.Assembly.GetName().Name;
        public static string FormatClrTypeName(Type t) => t.FullName!;
        public static string GetFullName(string typeName) => typeName.Split(',')[0];
      }
    }
    namespace Whizbang.Core.Messaging {
      public static class EventTypeMatchingHelper {
        public static string NormalizeTypeName(string n) => n;
      }
    }
    """;

  private static async Task<List<string>> _idsAsync(string body) {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<TypeNameHandlingAnalyzer>(PRELUDE + body);
    return [.. diagnostics.Where(d => d.Id.StartsWith("WHIZ16", StringComparison.Ordinal)).Select(d => d.Id)];
  }

  /// <summary>
  /// Substring on a type name is always a hand parse, whatever the argument, so it is the one
  /// dissection the recognizer accepts without inspecting arguments at all.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_SubstringOnATypeName_ReportsWhiz161Async() {
    const string body = """
      public class Sample {
        public string Take(Type t) => t.FullName!.Substring(3);
      }
      """;
    await Assert.That(await _idsAsync(body)).Contains("WHIZ161")
      .Because("Substring on a type name is a hand parse regardless of its arguments");
  }

  /// <summary>
  /// A separator the helpers do not own is not a type-name dissection. Split on a semicolon is
  /// ordinary string work and must stay silent, or the rule fires on unrelated parsing.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_SplitOnAnUnownedSeparator_IsSilentAsync() {
    const string body = """
      public class Sample {
        public string[] Parts(Type t) => t.FullName!.Split(';');
      }
      """;
    await Assert.That(await _idsAsync(body)).DoesNotContain("WHIZ161")
      .Because("';' is not a separator the type-name helpers own, so splitting on it is ordinary string work");
  }

  /// <summary>
  /// A non-literal separator cannot be judged at compile time. The recognizer must decline rather
  /// than guess, otherwise a variable holding ';' would be reported as a type-name dissection.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_SeparatorFromAVariable_IsSilentAsync() {
    const string body = """
      public class Sample {
        public string[] Parts(Type t, char sep) => t.FullName!.Split(sep);
      }
      """;
    await Assert.That(await _idsAsync(body)).DoesNotContain("WHIZ161")
      .Because("a separator that is not a literal cannot be judged at compile time, so the recognizer declines");
  }

  /// <summary>
  /// Replace is a dissection only for the C# global:: prefix; replacing anything else is not
  /// taking a type name apart.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_ReplacingTheGlobalPrefix_ReportsWhiz161Async() {
    const string body = """
      public class Sample {
        public string Strip(Type t) => t.FullName!.Replace("global::", "");
      }
      """;
    await Assert.That(await _idsAsync(body)).Contains("WHIZ161")
      .Because("stripping the global:: prefix by hand is exactly the dissection the helpers own");
  }

  /// <summary>
  /// The receiver forms that count as a type name: typeof(X).FullName and x.GetType().Name. Both
  /// must be recognized, or a hand parse of either goes unreported.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_OnGetTypeReceiver_ReportsWhiz161Async() {
    const string body = """
      public class Sample {
        public string Take(object o) => o.GetType().FullName!.Substring(2);
      }
      """;
    await Assert.That(await _idsAsync(body)).Contains("WHIZ161")
      .Because("x.GetType().FullName is a type name just as typeof(X).FullName is");
  }

  /// <summary>
  /// Parentheses are noise around the expression, not part of it. The unwrapper exists so a hand
  /// parse cannot be hidden behind them; the null-forgiving form is covered by the Substring
  /// fixture above, which writes t.FullName! directly.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_BehindParentheses_StillReportsWhiz161Async() {
    const string body = """
      public class Sample {
        public string Take(Type t) => (t.FullName!).Substring(1);
      }
      """;
    await Assert.That(await _idsAsync(body)).Contains("WHIZ161")
      .Because("wrapping a hand parse in parentheses must not hide it from the recognizer");
  }

  /// <summary>
  /// A helper's own name must never be treated as a key. TypeNameFormatter ends in "formatter",
  /// so assigning to it is the framework doing its job, not a consumer hand-building a key.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task KeyAssignment_ToAHelperNamedTarget_IsSilentAsync() {
    const string body = """
      public class Sample {
        public string MyFormatter = "";
        public void Set() { MyFormatter = "A" + "B"; }
      }
      """;
    await Assert.That(await _idsAsync(body)).DoesNotContain("WHIZ163")
      .Because("a target whose name ends in 'formatter' is helper machinery, not a consumer-built key");
  }
}
