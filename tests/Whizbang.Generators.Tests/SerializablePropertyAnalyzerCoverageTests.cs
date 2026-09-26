using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage for <see cref="SerializablePropertyAnalyzer"/> paths the existing
/// <c>SerializablePropertyAnalyzerTests</c> never exercise: <c>_isMessageType</c>'s interface and
/// attribute scans running all the way to exhaustion for a type that is genuinely not a message.
/// </summary>
/// <remarks>
/// <c>_isObjectType</c>'s <c>Nullable&lt;object&gt;</c> arm is reached only from source the compiler
/// rejects, which is exactly the state an analyzer runs in while the file is being typed — see
/// <c>Analyzer_ExplicitNullableOfObjectProperty_ReportsWHIZ060Async</c> below for why it still has to
/// answer correctly. Well-formed code never reaches it: <c>System.Nullable&lt;T&gt;</c> constrains
/// <c>T : struct</c>, and the nullable reference annotation in <c>object?</c> produces no
/// <c>Nullable&lt;T&gt;</c> wrapper at the symbol level at all — it is caught by the plain
/// <c>SpecialType.System_Object</c> check a few lines above instead, which is what the existing
/// sibling test <c>Analyzer_CommandWithNullableObjectProperty_ReportsWHIZ060Async</c> pins.
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

  /// <summary>
  /// Written out longhand, <c>System.Nullable&lt;object&gt;</c> violates the <c>T : struct</c>
  /// constraint and the compiler rejects it (CS0453) — but Roslyn still binds the property to a
  /// constructed <c>Nullable&lt;T&gt;</c> symbol whose type argument is <c>object</c>, and an
  /// analyzer runs against exactly that half-bound model every keystroke in the IDE. The property is
  /// still an object-typed payload, so WHIZ060 is still the right answer: reporting it is what tells
  /// the author their message is not AOT-serializable, and it is the first thing they see while they
  /// are still editing the declaration. Getting this wrong in the other direction is worse than
  /// silence — <c>_isObjectType</c> crashing or misclassifying on a half-bound symbol takes the whole
  /// analyzer down with AD0001 for every other message type in the compilation.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Analyzer_ExplicitNullableOfObjectProperty_ReportsWHIZ060Async() {
    const string source = """
        using Whizbang.Core;

        namespace TestApp;

        public class PayloadEvent : IEvent {
          public System.Nullable<object> Payload { get; set; }
          public string Name { get; set; } = string.Empty;
        }
        """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<SerializablePropertyAnalyzer>(source);

    await Assert.That(diagnostics.Any(d => d.Id == "AD0001")).IsFalse()
      .Because("a type argument the compiler rejected must not throw inside the analyzer");

    var objectDiagnostics = diagnostics.Where(d => d.Id == "WHIZ060").ToArray();
    await Assert.That(objectDiagnostics.Length).IsEqualTo(1)
      .Because("exactly the Nullable<object> property is an object-typed payload; the string property alongside it is serializable and must not be flagged");
    await Assert.That(objectDiagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Payload")
      .Because("the report has to name the offending property, or the author cannot tell which member to change");
  }
}
