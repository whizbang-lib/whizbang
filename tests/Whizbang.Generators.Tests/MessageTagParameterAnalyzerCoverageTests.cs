using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="MessageTagParameterAnalyzer"/> paths the existing
/// <c>MessageTagParameterAnalyzerTests</c> never exercise: a non-class named type reaching the
/// symbol action, a subclass relying on the compiler-synthesized implicit constructor, and a
/// mismatched constructor parameter whose type matches no candidate property at all.
/// </summary>
/// <remarks>
/// One of this round's targets in this file is NOT covered here, because it is unreachable by
/// construction rather than merely untested: <c>_analyzeNamedType</c>'s
/// <c>typeSymbol.ToDisplayString() == MESSAGE_TAG_ATTRIBUTE_NAME =&gt; return</c>
/// (MessageTagParameterAnalyzer.cs:55-56), guarding against analyzing the
/// <c>MessageTagAttribute</c> base class itself. <c>_inheritsFromMessageTagAttribute</c>
/// (MessageTagParameterAnalyzer.cs:95-106) only walks a type's ANCESTORS — it starts at
/// <c>typeSymbol.BaseType</c> and never compares <c>typeSymbol</c> itself against the target
/// name. For line 56 to run, a symbol would have to be its own ancestor: a type whose
/// fully-qualified display string is <c>Whizbang.Core.Attributes.MessageTagAttribute</c> AND
/// whose base type's display string is also <c>Whizbang.Core.Attributes.MessageTagAttribute</c>.
/// No single type can satisfy that (the preceding <c>!_inheritsFromMessageTagAttribute</c> check
/// at line 50 already returns for the real base class, since its own base is
/// <c>System.Attribute</c>, not itself) short of two distinct symbols sharing one fully-qualified
/// name across assemblies — the kind of deliberately ambiguous, likely non-compiling
/// cross-assembly name collision that is outside the local-compilation techniques established
/// in earlier rounds (which cover a specific assembly name or a type that fails to resolve, not
/// a type resolving successfully while colliding in name with an unrelated symbol). No source
/// was found that reaches this branch.
/// </remarks>
/// <tests>Whizbang.Generators.Tests/Analyzers/MessageTagParameterAnalyzerTests.cs</tests>
[Category("Analyzers")]
public class MessageTagParameterAnalyzerCoverageTests {
  /// <summary>
  /// MessageTagAttribute's own AttributeUsage allows Class OR Struct targets — a struct message
  /// type can legally be decorated with a tag — but a struct can never itself BE a
  /// MessageTagAttribute subclass, since a struct cannot inherit from a class. If the TypeKind
  /// guard (MessageTagParameterAnalyzer.cs:45-47) regressed and let a struct named type reach
  /// the inheritance/constructor scan, any struct declared anywhere in a consumer's codebase
  /// (tagged or not) would risk spurious WHIZ090 noise on a type that was never a candidate for
  /// this rule in the first place.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task StructNamedType_NotAnalyzedAsSubclassAsync() {
    const string source = """
        namespace TestApp;

        public struct AlertMessage {
          public string Code;
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).IsEmpty()
      .Because("AlertMessage is a struct, which can never be a MessageTagAttribute subclass, so the TypeKind guard must reject it before any inheritance check runs");
  }

  /// <summary>
  /// A MessageTagAttribute subclass that declares no constructor of its own relies on the
  /// compiler-synthesized implicit parameterless constructor. Routing a message's tag metadata
  /// correctly depends on constructor parameter names matching property names — but an implicit
  /// constructor has no user-declared parameters to check. If the implicit-constructor skip
  /// (MessageTagParameterAnalyzer.cs:65-67) regressed, the analyzer would inspect a
  /// compiler-generated constructor that was never meant to be validated, at best wasting work
  /// and at worst reacting to synthesized metadata it wasn't designed to see.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task SubclassWithNoDeclaredConstructor_ImplicitCtorSkippedAsync() {
    const string source = """
        using System;
        using Whizbang.Core.Attributes;

        namespace TestApp;

        [AttributeUsage(AttributeTargets.Class)]
        public class MyTagAttribute : MessageTagAttribute {
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090")).IsEmpty()
      .Because("a subclass with no declared constructor has only the compiler-synthesized implicit constructor, which must be skipped rather than inspected for parameters");
  }

  /// <summary>
  /// When a mismatched constructor parameter's type matches no candidate property at all, the
  /// suggested rename must still fall back to the first settable property rather than giving up
  /// with a bare '?'. Getting the message tag's routing metadata wrong routes messages nowhere
  /// anyone is listening — a low-quality suggestion (or none at all) makes that misconfiguration
  /// harder for a developer to fix. If the type-match branch (MessageTagParameterAnalyzer.cs:
  /// 143-150) were the only path, a parameter whose type doesn't match any property (here, a
  /// bool parameter against int/string-typed properties) would suggest nothing.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ParameterTypeMatchesNoProperty_SuggestsFirstSettablePropertyAsync() {
    const string source = """
        using System;
        using Whizbang.Core.Attributes;

        namespace TestApp;

        [AttributeUsage(AttributeTargets.Class)]
        public class MyTagAttribute : MessageTagAttribute {
          public int Priority { get; set; }

          public MyTagAttribute(bool enabled) {
            Priority = enabled ? 1 : 0;
          }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<MessageTagParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ090").Count()).IsEqualTo(1)
      .Because("'enabled' matches no property by name, so the mismatch must still be reported even though no property's type matches the parameter's bool type");
    var diagnostic = diagnostics.First(d => d.Id == "WHIZ090");
    var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("priority")
      .Because("with no type-matching property available, the suggestion must fall back to the first settable property (Priority) rather than staying '?'");
    await Assert.That(message).Contains("Priority")
      .Because("the fallback suggestion must name the actual property (Priority), not merely produce some non-empty suggestion");
  }
}
