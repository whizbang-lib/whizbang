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

  /// <summary>
  /// A hand-built string handed to a parameter by position is still a key assignment when the
  /// parameter's name says so; the recognizer has to resolve the parameter from the call's symbol
  /// because no argument name is written.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Argument_HandBuiltStringToAKeyParameterByPosition_ReportsWhiz163Async() {
    const string body = """
      public class Sample {
        public void Store(string clrTypeName) { _ = clrTypeName; }
        public void Build(string ns, string name) => Store(ns + "." + name);
      }
      """;
    await Assert.That(await _idsAsync(body)).Contains("WHIZ163")
      .Because("the parameter is a type-name key even though the call never names it");
  }

  /// <summary>
  /// The same positional hand-built string into a parameter whose name carries no key marker is
  /// ordinary string work; only the resolved parameter name decides.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Argument_HandBuiltStringToAnOrdinaryParameterByPosition_IsSilentAsync() {
    const string body = """
      public class Sample {
        public void Describe(string label) { _ = label; }
        public void Build(string ns, string name) => Describe(ns + "." + name);
      }
      """;
    await Assert.That(await _idsAsync(body)).DoesNotContain("WHIZ163")
      .Because("a label is not a key, so the positional argument must stay silent");
  }

  // ---- the helper exemption, in each shape the analyzer inspects -----------------------------

  /// <summary>Inside a helper type, an interpolation that composes a name is the helper's job.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Interpolation_InsideAHelper_IsSilentAsync() {
    const string body = """
      public static class TypeNameFormatter {
        public static string Qualified(Type t, string assembly) => $"{t.FullName}, {assembly}";
      }
      """;
    await Assert.That(await _idsAsync(body)).IsEmpty()
      .Because("the helpers are where names are composed; the rules exist to keep composition there");
  }

  /// <summary>Inside a helper type, assigning a key from a built string is the helper's job.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Assignment_InsideAHelper_IsSilentAsync() {
    const string body = """
      public class Row { public string ClrTypeName { get; set; } = ""; }
      public static class TypeNameFormatter {
        public static void Fill(Row row, string ns, string name) { row.ClrTypeName = ns + "." + name; }
      }
      """;
    await Assert.That(await _idsAsync(body)).IsEmpty();
  }

  /// <summary>A key declared without an initializer, or assigned from a plain literal, is not built by hand.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Declarator_WithoutAnInitializer_IsSilentAsync() {
    const string body = """
      public class Sample {
        public string Build() {
          string clrTypeName;
          clrTypeName = "Fixed.Name";
          return clrTypeName;
        }
      }
      """;
    await Assert.That(await _idsAsync(body)).IsEmpty()
      .Because("only a string built from parts is a hand-composed key");
  }

  // ---- dissection and argument shapes ----------------------------------------------------------

  /// <summary>Split with no arguments is whitespace splitting, not a dissection on a separator the helpers own.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Dissection_SplitWithNoArguments_IsSilentAsync() {
    const string body = """
      public class Sample {
        public string[] Words(Type t) => t.FullName!.Split();
      }
      """;
    await Assert.That(await _idsAsync(body)).DoesNotContain("WHIZ161");
  }

  /// <summary>A hand-built string inside a tuple is an argument of no method, so no parameter name can make it a key.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Argument_InsideATuple_IsSilentAsync() {
    const string body = """
      public class Sample {
        public (string, int) Pair(string ns, string name) => (ns + "." + name, 1);
      }
      """;
    await Assert.That(await _idsAsync(body)).DoesNotContain("WHIZ163");
  }

  /// <summary>Invoking a delegate has no parameter names to consult, so a hand-built argument stays silent.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Argument_ToADelegate_IsSilentAsync() {
    const string body = """
      public class Sample {
        public void Build(Action<string> store, string ns, string name) => store(ns + "." + name);
      }
      """;
    await Assert.That(await _idsAsync(body)).DoesNotContain("WHIZ163");
  }

  /// <summary>Parentheses around the built string, or around one of its parts, do not hide the composition.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Argument_ParenthesizedHandBuiltString_IsStillReportedAsync() {
    const string body = """
      public class Sample {
        public void Store(string clrTypeName) { _ = clrTypeName; }
        public void Build(string ns, string name) {
          Store((ns + "." + name));
          Store("prefix" + (ns + name));
        }
      }
      """;
    await Assert.That((await _idsAsync(body)).Count(id => id == "WHIZ163")).IsEqualTo(2)
      .Because("a reader cannot tell a parenthesized composition from a bare one, and neither can the key it lands in");
  }

  // ---- what counts as a type-name value ---------------------------------------------------------

  /// <summary>typeof(X).FullName is a type-name value, so composing an assembly onto it is a hand-built name.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Composition_OnTypeofFullName_ReportsWhiz160Async() {
    const string body = """
      public class Sample {
        public string Name(string assembly) => typeof(Sample).FullName + ", " + assembly;
      }
      """;
    await Assert.That(await _idsAsync(body)).Contains("WHIZ160")
      .Because("the compiler knows the type; a name built beside it bypasses the formatter that owns the form");
  }

  /// <summary>What a helper returns is a type-name value, whether the helper is named plainly or qualified.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Composition_OnAHelperResult_ReportsWhiz160Async() {
    const string body = """
      public class Sample {
        public string Plain(Type t, string assembly) => Whizbang.Core.TypeNameFormatter.FormatClrTypeName(t) + ", " + assembly;
      }
      namespace Inner {
        using Whizbang.Core;
        public class Qualified {
          public string Name(Type t, string assembly) => TypeNameFormatter.FormatClrTypeName(t) + ", " + assembly;
        }
      }
      """;
    await Assert.That((await _idsAsync(body)).Count(id => id == "WHIZ160")).IsEqualTo(2)
      .Because("the helper produced a name; appending to it composes a new one by hand in either spelling");
  }

  /// <summary>An argument past the end of the parameter list (a params expansion) has no parameter of its own to name it.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Argument_BeyondTheParameterList_IsJudgedByTheParamsParameterOnlyOnceAsync() {
    const string body = """
      public class Sample {
        public void Store(params string[] clrTypeNames) { _ = clrTypeNames; }
        public void Build(string ns, string name) => Store(ns + "." + name, ns + "." + name);
      }
      """;
    await Assert.That((await _idsAsync(body)).Count(id => id == "WHIZ163")).IsEqualTo(1)
      .Because("the first argument binds to the params parameter and carries its name; the second is an expansion with no parameter to consult");
  }
}
