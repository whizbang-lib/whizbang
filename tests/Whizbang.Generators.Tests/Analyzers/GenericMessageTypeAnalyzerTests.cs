// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ015 reports a generic message type where it is declared, because the generators skip it (#1481).
/// </summary>
[Category("Analyzers")]
public class GenericMessageTypeAnalyzerTests {
  private const string ID = "WHIZ015";

  /// <summary>A generic type implementing <c>IMessage</c> through a derived interface is reported at its name.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AGenericMessage_IsReportedAtItsDeclarationAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      namespace Shop;
      public interface IAnswer : IMessage { Guid Id { get; } }
      public record GenericAnswer<T>(Guid Id, T Value) : IAnswer;
      """;

    var diagnostics = (await AnalyzerTestHelper.GetDiagnosticsAsync<GenericMessageTypeAnalyzer>(source))
      .Where(d => d.Id == ID).ToList();

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    var location = diagnostics[0].Location;
    await Assert.That(location.IsInSource).IsTrue();
    await Assert.That(location.SourceTree!.ToString().Substring(location.SourceSpan.Start, location.SourceSpan.Length))
      .IsEqualTo("GenericAnswer");
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("GenericAnswer<T>");
  }

  /// <summary>Generic events and commands are reported too.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AGenericEventAndCommand_AreBothReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace Shop;
      public record GenericEvent<T>(T Value) : IEvent;
      public class GenericCommand<T> : ICommand { public T? Value { get; init; } }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GenericMessageTypeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == ID)).IsEqualTo(2);
  }

  /// <summary>Non-generic messages, closed forms and generic types that are not messages are not reported.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonGenericMessagesAndNonMessages_AreNotReportedAsync() {
    const string source = """
      using System.Collections.Generic;
      using Whizbang.Core;
      namespace Shop;
      public record GenericEvent<T>(T Value) : IEvent;
      public record IntEvent(int Value) : GenericEvent<int>(Value);
      public record OrderPlaced : IEvent;
      public class Bag<T> { public List<T> Items { get; } = []; }
      public interface IAnswer<T> : IMessage { }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<GenericMessageTypeAnalyzer>(source);

    // Only GenericEvent<T>; IntEvent is closed, OrderPlaced is not generic, Bag<T> is not a message, and an
    // interface is not a message type the generators would name.
    await Assert.That(diagnostics.Count(d => d.Id == ID)).IsEqualTo(1);
  }
}
