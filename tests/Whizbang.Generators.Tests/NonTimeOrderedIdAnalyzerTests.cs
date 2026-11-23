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
    """;

  [RequiresAssemblyFiles]
  private static async Task<Diagnostic[]> _diagnosticsAsync(string body) {
    var source = PRELUDE + "\npublic static class Probe {\n" + body + "\n}\n";
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<NonTimeOrderedIdAnalyzer>(source);
    return [.. diagnostics.Where(d => d.Id == "WHIZ037")];
  }

  public static IEnumerable<Func<string>> FlaggedShapes() {
    // new Guid(byte[]) into a [StreamId] member
    yield return () => "public static OrderPlaced Run(byte[] b) => new OrderPlaced { OrderId = new Guid(b) };";
    // new Guid("literal") into a message id property
    yield return () => "public static void Run(Envelope e) { e.MessageId = new Guid(\"919108f7-52d1-4320-9bac-f847db4148a8\"); }";
    // Guid.Parse into an event id property
    yield return () => "public static void Run(Envelope e, string s) { e.EventId = Guid.Parse(s); }";
    // Guid.Parse into MessageId.From
    yield return () => "public static MessageId Run(string s) => MessageId.From(Guid.Parse(s));";
    // new Guid(bytes) into the MessageId constructor
    yield return () => "public static MessageId Run(byte[] b) => new MessageId(new Guid(b));";
    // through a local
    yield return () => "public static void Run(Envelope e, string s) { var id = Guid.Parse(s); e.MessageId = id; }";
    // Guid.TryParse's out value
    yield return () => "public static void Run(Envelope e, string s) { if (Guid.TryParse(s, out var id)) { e.MessageId = id; } }";
    // EventId.From
    yield return () => "public static EventId Run(byte[] b) => EventId.From(new Guid(b));";
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
    yield return () => "public static OrderPlaced Run(MessageId from) => new OrderPlaced { OrderId = from.Value };";
    // A parse into a property that is no id
    yield return () => "public static void Run(Envelope e, string s) { e.Other = Guid.Parse(s); }";
    // A parse that never reaches an id
    yield return () => "public static Guid Run(string s) => Guid.Parse(s);";
    // A local initialized from TrackedGuid.New() is quiet
    yield return () => "public static void Run(Envelope e) { var id = (Guid)TrackedGuid.New(); e.MessageId = id; }";
  }

  [Test]
  [RequiresAssemblyFiles]
  [MethodDataSource(nameof(QuietShapes))]
  public async Task QuietShape_ReportsNothingAsync(string body) {
    var diagnostics = await _diagnosticsAsync(body);

    await Assert.That(diagnostics).IsEmpty();
  }

  /// <summary>A derived id (the framework's derivation from a source message id) is v7 and is quiet.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task DerivedId_IsQuietAsync() {
    var diagnostics = await _diagnosticsAsync(
      "public static OrderPlaced Run(MessageId source) => new OrderPlaced { OrderId = GeneratedStreamIdentity.Derive(source.Value) };");

    await Assert.That(diagnostics).IsEmpty();
  }
}
