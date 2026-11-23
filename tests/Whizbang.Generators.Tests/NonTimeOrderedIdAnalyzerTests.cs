// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ037 (#2528): an id built from bytes, a string literal or a parse flows into a message id, an event id or a
/// <c>[StreamId]</c> member. Interception cannot reach a constructor or a value parsed from the wire, so this is the
/// compile-time warning for those shapes; it stays quiet on the framework's own minting (<c>TrackedGuid.New()</c>, a
/// strong id's <c>New()</c>, a derived id).
/// </summary>
[Category("Analyzers")]
public class NonTimeOrderedIdAnalyzerTests {
  private const string PRELUDE = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.ValueObjects;

    namespace App;

    public sealed record OrderPlaced : IEvent {
      [StreamId]
      public Guid OrderId { get; init; }
    }

    public sealed class Envelope {
      public Guid MessageId { get; set; }
      public Guid EventId { get; set; }
      public Guid Other { get; set; }
    }

    public sealed class FieldEnvelope {
      public Guid MessageId;
    }
    """;

  [RequiresAssemblyFiles]
  private static async Task<Diagnostic[]> _diagnosticsAsync(string body) {
    var source = PRELUDE + "\npublic static class Probe {\n" + body + "\n}\n";
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source);
    // A body that does not compile binds nothing, and silence from it would prove nothing about the analyzer
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length > 0) {
      throw new InvalidOperationException("The probe does not compile: " + string.Join("; ", errors.Select(e => e.ToString())));
    }
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<NonTimeOrderedIdAnalyzer>(compilation);
    return [.. diagnostics.Where(d => d.Id == "WHIZ037")];
  }

  public static IEnumerable<Func<string>> FlaggedShapes() {
    // new Guid(byte[]) into a [StreamId] member
    yield return () => "public static OrderPlaced Run(byte[] b) => new OrderPlaced { OrderId = new Guid(b) };";
    // new Guid("literal") into a message id property
    yield return () => "public static void Run(Envelope e) { e.MessageId = new Guid(\"919108f7-52d1-4320-9bac-f847db4148a8\"); }";
    // Guid.Parse into an event id property
    yield return () => "public static void Run(Envelope e, string s) { e.EventId = Guid.Parse(s); }";
    // new Guid(bytes) into the MessageId constructor
    yield return () => "public static MessageId Run(byte[] b) => new MessageId(new Guid(b));";
    // an interpolated string whose head is not a UUIDv7's
    yield return () => "public static void Run(Envelope e, int n) { e.EventId = Guid.Parse($\"0199b000-0000-4000-8000-{n:D12}\"); }";
    // an interpolated string with no fixed head
    yield return () => "public static void Run(Envelope e, string s) { e.EventId = Guid.Parse($\"{s}\"); }";
    // through a local
    yield return () => "public static void Run(Envelope e, string s) { var id = Guid.Parse(s); e.MessageId = id; }";
    // Guid.TryParse's out value
    yield return () => "public static void Run(Envelope e, string s) { if (Guid.TryParse(s, out var id)) { e.MessageId = id; } }";
    // new Guid(bytes) into the EventId constructor
    yield return () => "public static EventId Run(byte[] b) => new EventId(new Guid(b));";
    // new Guid(bytes) into a message id field
    yield return () => "public static void Run(FieldEnvelope e, byte[] b) { e.MessageId = new Guid(b); }";
    // a constant that is not a Guid at all carries no version
    yield return () => "public static void Run(Envelope e) { e.MessageId = Guid.Parse(\"not-a-guid\"); }";
    // ---- a local declared without an initializer and assigned later ----
    yield return () => "public static void Run(Envelope e, string s) { Guid id; id = Guid.Parse(s); e.MessageId = id; }";
    yield return () => "public static void Run(Envelope e, byte[] b) { Guid id; id = new Guid(b); e.MessageId = id; }";
    yield return () => "public static void Run(Envelope e) { Guid id; id = new Guid(\"919108f7-52d1-4320-9bac-f847db4148a8\"); e.MessageId = id; }";
    // Guid.TryParse writing to an existing local
    yield return () => "public static void Run(Envelope e, string s) { Guid id; Guid.TryParse(s, out id); e.MessageId = id; }";
    yield return () => "public static void Run(Envelope e, string s) { Guid id = default; if (Guid.TryParse(s, out id)) { e.EventId = id; } }";
    // a clean value replaced by a flagged one before the use
    yield return () => "public static void Run(Envelope e, string s) { var id = (Guid)TrackedGuid.New(); id = Guid.Parse(s); e.MessageId = id; }";
    // branches: one arm flagged, the other clean
    yield return () => "public static void Run(Envelope e, string s, bool c) { Guid id; if (c) { id = Guid.Parse(s); } else { id = TrackedGuid.New(); } e.MessageId = id; }";
    // a one-armed branch that may replace a clean value with a flagged one
    yield return () => "public static void Run(Envelope e, string s, bool c) { Guid id = TrackedGuid.New(); if (c) { id = Guid.Parse(s); } e.MessageId = id; }";
    // a loop's back edge carries an assignment after the use around to it
    yield return () => "public static void Run(Envelope e, string s, int n) { Guid id = TrackedGuid.New(); for (var i = 0; i < n; i++) { e.MessageId = id; id = Guid.Parse(s); } }";
    // into a [StreamId] member through an object initializer
    yield return () => "public static OrderPlaced Run(byte[] b) { Guid id; id = new Guid(b); return new OrderPlaced { OrderId = id }; }";
    // into the unchecked MessageId constructor
    yield return () => "public static MessageId Run(string s) { Guid id; id = Guid.Parse(s); return new MessageId(id); }";
    // a lambda capturing a local assigned in the enclosing method: any of its assignments may be the one captured
    yield return () => "public static Action Run(Envelope e, string s) { var id = Guid.Parse(s); return () => e.MessageId = id; }";
    yield return () => "public static Action Run(Envelope e, string s) { Guid id = TrackedGuid.New(); Action a = () => e.MessageId = id; id = Guid.Parse(s); return a; }";
    // a local function capturing one
    yield return () => "public static void Run(Envelope e, string s) { Guid id; id = Guid.Parse(s); Set(); void Set() => e.MessageId = id; }";
    // the whole flow inside a lambda
    yield return () => "public static Action<string> Run(Envelope e) => s => { Guid id; id = Guid.Parse(s); e.MessageId = id; };";
    // a catch block may follow any assignment in its try
    yield return () => "public static void Run(Envelope e, string s) { Guid id = TrackedGuid.New(); try { id = Guid.Parse(s); id = TrackedGuid.New(); } catch (FormatException) { e.MessageId = id; } }";
  }

  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(FlaggedShapes))]
  public async Task FlaggedShape_ReportsWHIZ037WarningAsync(string body) {
    var diagnostics = await _diagnosticsAsync(body);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
  }

  public static IEnumerable<Func<string>> QuietShapes() {
    yield return () => "public static OrderPlaced Run() => new OrderPlaced { OrderId = TrackedGuid.New() };";
    yield return () => "public static void Run(Envelope e) { e.MessageId = MessageId.New(); }";
    yield return () => "public static MessageId Run() => MessageId.New();";
    // From refuses a non-v7 Guid at run time, so it is not reported
    yield return () => "public static MessageId Run(string s) => MessageId.From(Guid.Parse(s));";
    // A literal that is itself a UUIDv7
    yield return () => "public static void Run(Envelope e) { e.MessageId = Guid.Parse(\"01900000-0000-7000-8000-000000000001\"); }";
    yield return () => "public static OrderPlaced Run(MessageId from) => new OrderPlaced { OrderId = from.Value };";
    // A parse into a property that is no id
    yield return () => "public static void Run(Envelope e, string s) { e.Other = Guid.Parse(s); }";
    // A parse that never reaches an id
    yield return () => "public static Guid Run(string s) => Guid.Parse(s);";
    // An interpolated string whose fixed head is a UUIDv7's
    yield return () => "public static void Run(Envelope e, int n) { e.EventId = Guid.Parse($\"0199b000-0000-7000-8000-{n:D12}\"); }";
    // A local initialized from TrackedGuid.New() is quiet
    yield return () => "public static void Run(Envelope e) { var id = (Guid)TrackedGuid.New(); e.MessageId = id; }";
    // An assignment to a local is no id member
    yield return () => "public static Guid Run(string s) { Guid id; id = Guid.Parse(s); return id; }";
    // ---- assignments after the declaration: only the assignments that reach the use count ----
    // a flagged value replaced by a clean one before the use: the clean one is the only one reaching it
    yield return () => "public static void Run(Envelope e, string s) { var id = Guid.Parse(s); id = TrackedGuid.New(); e.MessageId = id; }";
    yield return () => "public static void Run(Envelope e, string s) { Guid id; id = Guid.Parse(s); id = MessageId.New().Value; e.MessageId = id; }";
    // a flagged assignment after the use, outside any loop, never reaches it
    yield return () => "public static void Run(Envelope e, string s) { Guid id = TrackedGuid.New(); e.MessageId = id; id = Guid.Parse(s); e.Other = id; }";
    // both arms clean
    yield return () => "public static void Run(Envelope e, bool c) { Guid id; if (c) { id = TrackedGuid.New(); } else { id = MessageId.New().Value; } e.MessageId = id; }";
    // a flagged value in one arm, replaced by a clean one before the arms join
    yield return () => "public static void Run(Envelope e, string s, bool c) { Guid id = TrackedGuid.New(); if (c) { id = Guid.Parse(s); id = TrackedGuid.New(); } e.MessageId = id; }";
    // a loop that only ever assigns clean values
    yield return () => "public static void Run(Envelope e, int n) { Guid id = TrackedGuid.New(); for (var i = 0; i < n; i++) { e.MessageId = id; id = TrackedGuid.New(); } }";
    // the framework's derivations, From, and a v7 literal, assigned later
    yield return () => "public static void Run(Envelope e, MessageId source) { Guid id; id = GeneratedStreamIdentity.Derive(source.Value); e.MessageId = id; }";
    yield return () => "public static void Run(Envelope e, MessageId source) { Guid id; id = Whizbang.Core.Messaging.EmissionIdentity.Derive(source.Value, \"svc\", null, \"T\", 0); e.MessageId = id; }";
    yield return () => "public static void Run(Envelope e, MessageId source) { Guid id; id = Whizbang.Core.Messaging.CompositeChildIdentity.ForChild(source.Value, 0, \"T\"); e.MessageId = id; }";
    yield return () => "public static void Run(Envelope e, string s) { Guid id; id = MessageId.From(Guid.Parse(s)).Value; e.MessageId = id; }";
    yield return () => "public static void Run(Envelope e) { Guid id; id = Guid.Parse(\"01900000-0000-7000-8000-000000000001\"); e.MessageId = id; }";
    // a parameter is not followed, even when the method assigns it: its value is the caller's, and following it means
    // following every call site, which this analyzer does not do
    yield return () => "public static void Run(Envelope e, Guid p) { e.MessageId = p; }";
    yield return () => "public static void Run(Envelope e, Guid p, string s) { p = Guid.Parse(s); e.MessageId = p; }";
    // a local copied from a field: the field is not followed (its writers are anywhere in the type)
    yield return () => "private static Guid _field = Guid.Parse(\"919108f7-52d1-4320-9bac-f847db4148a8\");\npublic static void Run(Envelope e) { var id = _field; e.MessageId = id; }";
    yield return () => "private static Guid _field;\npublic static void Run(Envelope e) { Guid id; id = _field; e.MessageId = id; }";
    // an out argument of a method other than Guid.TryParse, into an existing local
    yield return () => "public static void Run(Envelope e) { Guid id; Fill(out id); e.MessageId = id; }\nprivate static void Fill(out Guid g) => g = TrackedGuid.New();";
    // a lambda whose local is its own and clean
    yield return () => "public static Action Run(Envelope e) => () => { Guid id; id = TrackedGuid.New(); e.MessageId = id; };";
    // A local that is the out argument of a method other than Guid.TryParse
    yield return () => "public static void Run(Envelope e) { Fill(out var id); e.MessageId = id; }\nprivate static void Fill(out Guid g) => g = TrackedGuid.New();";
  }

  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(QuietShapes))]
  public async Task QuietShape_ReportsNothingAsync(string body) {
    var diagnostics = await _diagnosticsAsync(body);

    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// Nested loops reassigning the local on every level converge (the analysis only ever adds assignments to the sets it
  /// carries), and report once per use.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NestedLoops_ConvergeAndReportOncePerUseAsync() {
    var diagnostics = await _diagnosticsAsync("""
      public static void Run(Envelope e, string s, int n) {
        Guid id = TrackedGuid.New();
        for (var a = 0; a < n; a++) {
          while (n > a) {
            do {
              foreach (var c in s) { e.MessageId = id; id = TrackedGuid.New(); }
              id = c0(s);
            } while (n-- > 0);
            id = TrackedGuid.New();
          }
          id = Guid.Parse(s);
        }
        e.EventId = id;
      }
      private static Guid c0(string s) => TrackedGuid.New();
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(2)
      .Because("the parse at the outer loop's end reaches both uses: round the back edges to the first, and out of the loop to the second");
  }

  /// <summary>A derived id (the framework's derivation from a source message id) is v7 and is quiet.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task DerivedId_IsQuietAsync() {
    var diagnostics = await _diagnosticsAsync(
      "public static OrderPlaced Run(MessageId source) => new OrderPlaced { OrderId = GeneratedStreamIdentity.Derive(source.Value) };");

    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>
  /// A method or type carrying <c>[SuppressGuidOrderingValidation]</c> is a deliberate opt-out: WHIZ037 is quiet
  /// inside it (in a local function or lambda within it too), and still reports in the code beside it.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AMethodCarryingTheSuppression_IsQuiet_AndItsNeighborStillReportsAsync() {
    var diagnostics = await _diagnosticsAsync("""
      [SuppressGuidOrderingValidation]
      public static void Legacy(Envelope e, string s) { e.MessageId = Guid.Parse(s); }
      [SuppressGuidOrderingValidation]
      public static void LegacyLocal(Envelope e, string s) { void Set() => e.EventId = Guid.Parse(s); Set(); }
      public static void Unmarked(Envelope e, string s) { e.MessageId = Guid.Parse(s); }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1)
      .Because("only the unmarked method reports");
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("MessageId");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ATypeCarryingTheSuppression_IsQuietInEveryMemberAsync() {
    var source = PRELUDE + """

      [SuppressGuidOrderingValidation]
      public sealed class LegacyImporter {
        public void Run(Envelope e, string s) { e.MessageId = Guid.Parse(s); }
        public sealed class Nested {
          public static MessageId Run(byte[] b) => new MessageId(new Guid(b));
        }
      }
      """;
    var diagnostics = (await AnalyzerTestHelper.GetDiagnosticsAsync<NonTimeOrderedIdAnalyzer>(source))
      .Where(d => d.Id == "WHIZ037").ToArray();

    await Assert.That(diagnostics).IsEmpty();
  }
}
