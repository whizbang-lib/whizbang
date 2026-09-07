using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="SerializablePropertyAnalyzer"/> paths the existing
/// <c>SerializablePropertyAnalyzerTests</c> never exercise: <c>_isMessageType</c>'s interface and
/// attribute scans running all the way to exhaustion for a type that is genuinely not a message.
/// </summary>
/// <remarks>
/// One of this round's targets in this file is NOT covered here, because it is unreachable by
/// construction rather than merely untested: <c>_isObjectType</c>'s <c>Nullable&lt;object&gt;</c>
/// check — the compound condition at SerializablePropertyAnalyzer.cs:176-179 and its
/// <c>return true;</c> at line 180. <c>System.Nullable&lt;T&gt;</c> constrains <c>T : struct</c>, and
/// <c>object</c> is a reference type — <c>Nullable&lt;object&gt;</c> cannot be expressed by any
/// valid C# type argument, so no <see cref="INamedTypeSymbol"/> with
/// <c>OriginalDefinition.SpecialType == SpecialType.System_Nullable_T</c> can ever have a type
/// argument whose <c>SpecialType</c> is <c>System_Object</c>. This is corroborated by the
/// existing sibling test <c>Analyzer_CommandWithNullableObjectProperty_ReportsWHIZ060Async</c>,
/// whose own comment notes "object? in records is still just object in IL" — a nullable
/// reference-type annotation on <c>object</c> never produces a <c>Nullable&lt;T&gt;</c> wrapper
/// at the symbol level in the first place; it is caught by the plain
/// <c>SpecialType.System_Object</c> check a few lines above instead. No source was found that
/// reaches this branch.
/// </remarks>
/// <tests>Whizbang.Generators.Tests/SerializablePropertyAnalyzerTests.cs</tests>
[Category("Analyzers")]
public class SerializablePropertyAnalyzerCoverageTests {
  /// <summary>
  /// A public type that implements some unrelated interface and carries some unrelated
  /// attribute — but is neither an <c>ICommand</c>/<c>IEvent</c> nor <c>[WhizbangSerializable]</c>
  /// — must never be treated as a message type, no matter what its properties look like.
  /// <c>_isMessageType</c>'s interface scan (SerializablePropertyAnalyzer.cs:152-157) and
  /// attribute scan (SerializablePropertyAnalyzer.cs:160-164) must each walk their full,
  /// non-matching list to exhaustion and fall through to "not a message type" rather than
  /// stopping early. This is the false-positive half of the rule: if either scan misfired here,
  /// an ordinary, non-message type with an incidental interface or attribute would start being
  /// walked for "risky" properties it was never a candidate for, flagging object-typed
  /// properties (like <c>Payload</c> below) that have nothing to do with message serialization.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonMessageTypeWithUnrelatedInterfaceAndAttribute_NoDiagnosticsAsync() {
    const string source = """
        using System;

        namespace TestApp;

        [Obsolete("legacy")]
        public class NotAMessage : IDisposable {
          public object Payload { get; set; } = new();

          public void Dispose() { }
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    var ours = diagnostics.Where(d => d.Id is "WHIZ060" or "WHIZ061" or "WHIZ062" or "WHIZ063" or "AD0001").ToArray();
    await Assert.That(ours.Length).IsEqualTo(0)
      .Because("NotAMessage implements only IDisposable (not ICommand/IEvent) and carries only [Obsolete] (not [WhizbangSerializable]), so both exhaustive scans in _isMessageType must conclude it is not a message type and leave its object-typed Payload property unreported; AD0001 would indicate the analyzer crashed instead of returning false cleanly");
  }
}
