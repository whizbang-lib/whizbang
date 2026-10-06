// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// The remaining shapes of the type-name recognizers: how the <c>string.Equals</c> receiver is
/// spelled, a helper reached through its namespace, a <c>FullName</c> read from something other than
/// <see cref="System.Type"/>, a key-named operand that has no type, and a named argument.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/TypeNameHandlingAnalyzer.cs</code-under-test>
/// <docs>operations/diagnostics/whiz160</docs>
public class TypeNameHandlingAnalyzerBranchTests {
  private const string PRELUDE = """
    using System;
    namespace Whizbang.Core {
      public static class TypeNameFormatter {
        public static string Format(Type t) => t.FullName!;
      }
    }
    namespace Other {
      public class Type { public string FullName { get; set; } = ""; }
      public class Person { public string FullName { get; set; } = ""; }
      public static class Text { public static bool Equals(string a, string b) => a == b; }
    }
    public static class LocalText { public static bool Equals(string a, string b) => a == b; }
    """;

  private static async Task<List<string>> _idsAsync(string body) {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<TypeNameHandlingAnalyzer>(PRELUDE + body);
    return [.. diagnostics.Where(d => d.Id.StartsWith("WHIZ16", StringComparison.Ordinal)).Select(d => d.Id)];
  }

  private static string _equalsWith(string receiver) => $$"""
    public class Sample {
      public bool Same(Type a, Type b) => {{receiver}}.Equals(a.FullName, b.FullName);
    }
    """;

  /// <summary>
  /// <c>string.Equals</c> between two type names is WHIZ162 however the receiver is spelled: the
  /// keyword, the type name, or the namespace-qualified type name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("string")]
  [Arguments("String")]
  [Arguments("System.String")]
  public async Task StringEquals_AnySpellingOfString_ReportsWhiz162Async(string receiver) {
    await Assert.That(await _idsAsync(_equalsWith(receiver))).Contains("WHIZ162");
  }

  /// <summary>
  /// An <c>Equals</c> on any other receiver is not <c>string.Equals</c>: another keyword type, a
  /// type that is not <c>String</c>, or a qualified name whose last part is not <c>String</c>.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("object")]
  [Arguments("LocalText")]
  [Arguments("Other.Text")]
  public async Task Equals_OnAnotherReceiver_IsNotWhiz162Async(string receiver) {
    await Assert.That(await _idsAsync(_equalsWith(receiver))).DoesNotContain("WHIZ162");
  }

  /// <summary>
  /// A call on a shared helper is a type-name value whether the helper is named bare or through
  /// its namespace; a call on any other type reached through a namespace is not.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task QualifiedHelperCall_IsATypeNameValue_AndAnotherQualifiedCallIsNotAsync() {
    var helper = await _idsAsync("""
      public class Sample {
        public bool Same(Type a, Type b) => string.Equals(Whizbang.Core.TypeNameFormatter.Format(a), Whizbang.Core.TypeNameFormatter.Format(b));
      }
      """);
    var other = await _idsAsync("""
      public class Sample {
        public bool Same(string a, string b) => string.Equals(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b));
      }
      """);

    await Assert.That(helper).Contains("WHIZ162");
    await Assert.That(other).DoesNotContain("WHIZ162");
  }

  /// <summary>
  /// A call on any other receiver is not a type-name value: a type named bare that is not a helper,
  /// or a receiver that is itself a call.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task CallOnANonHelperReceiver_IsNotATypeNameValueAsync() {
    var bareType = await _idsAsync("""
      public class Sample {
        public bool Same(Type a, Type b) => string.Equals(Convert.ToString(a), Convert.ToString(b));
      }
      """);
    var chained = await _idsAsync("""
      public class Sample {
        private static string Make() => "";
        public bool Same() => string.Equals(Make().Trim(), Make().Trim());
      }
      """);

    await Assert.That(bareType).DoesNotContain("WHIZ162");
    await Assert.That(chained).DoesNotContain("WHIZ162");
  }

  /// <summary>
  /// <c>FullName</c> is a type-name value only when read from <see cref="System.Type"/>: a type
  /// named <c>Type</c> in another namespace, and a person's full name, are not.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task FullName_OnlyFromSystemType_IsATypeNameValueAsync() {
    var system = await _idsAsync("""
      public class Sample { public string Cut(Type t) => t.FullName!.Substring(1); }
      """);
    var lookAlike = await _idsAsync("""
      public class Sample { public string Cut(Other.Type t) => t.FullName.Substring(1); }
      """);
    var person = await _idsAsync("""
      public class Sample { public string Cut(Other.Person p) => p.FullName.Substring(1); }
      """);

    await Assert.That(system).Contains("WHIZ161");
    await Assert.That(lookAlike).DoesNotContain("WHIZ161");
    await Assert.That(person).DoesNotContain("WHIZ161");
  }

  /// <summary>
  /// Comparing two key-named operands is WHIZ162 only when both are strings; a key-named method
  /// group has no type at all and is not compared as a type name.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task KeyNamedOperandWithoutAType_IsNotComparedAsATypeNameAsync() {
    var ids = await _idsAsync("""
      public class Sample {
        public string ClrTypeName() => "";
        public bool Same(Sample other) => ClrTypeName == other.ClrTypeName;
      }
      """);

    await Assert.That(ids).DoesNotContain("WHIZ162");
  }

  /// <summary>
  /// A hand-built string passed by name to a key-named parameter is WHIZ163, read from the name
  /// written at the call rather than resolved from the method.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NamedArgumentToAKeyParameter_ReportsWhiz163Async() {
    var ids = await _idsAsync("""
      public class Sample {
        public void Store(string clrTypeName) { }
        public void Run(string ns, string name) => Store(clrTypeName: $"{ns}.{name}");
      }
      """);

    await Assert.That(ids).Contains("WHIZ163");
  }
}
