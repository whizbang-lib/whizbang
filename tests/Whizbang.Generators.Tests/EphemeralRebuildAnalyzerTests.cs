// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ131: a call site that asks to rebuild or rewind a perspective whose every applied event is ephemeral. Such a
/// perspective is authoritative state, never a rebuildable cache: its events self-destruct and their bodies are reaped,
/// so the rebuild or rewind has nothing to replay. The three call sites are constructing <c>RebuildPerspectiveCommand</c>
/// for it, a rebuilder call for it and a rewind request against it; the perspective is named by a compile-time constant
/// string (its CLR type name, the name the runtime registry matches) or by <c>typeof(T).FullName</c>.
/// </summary>
/// <tests>src/Whizbang.Generators/EphemeralAnalyzer.cs</tests>
[Category("Analyzers")]
public class EphemeralRebuildAnalyzerTests {
  private const string HEADER = """
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Commands.System;
    using Whizbang.Core.Perspectives;
    namespace TestApp;
    public class Model { public Guid Id { get; set; } }
    [Ephemeral]
    public record PresencePing : IEvent;
    [Ephemeral]
    public record PresenceGone : IEvent;
    public record OrderPlaced : IEvent;
    public class PresenceProjection
      : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, PresenceGone> {
      public Model Apply(Model current, PresencePing e) => current;
      public Model Apply(Model current, PresenceGone e) => current;
    }
    [DangerouslyAllowMixedEphemeralAndSourcedEvents]
    public class MixedProjection
      : IPerspectiveFor<Model, PresencePing>, IPerspectiveFor<Model, OrderPlaced> {
      public Model Apply(Model current, PresencePing e) => current;
      public Model Apply(Model current, OrderPlaced e) => current;
    }
    public class OrderProjection : IPerspectiveFor<Model, OrderPlaced> {
      public Model Apply(Model current, OrderPlaced e) => current;
    }

    """;

  // Every source must compile: a call the compiler could not bind would leave a negative test passing for the wrong
  // reason.
  private static async Task<Diagnostic[]> _whiz131Async(string body) {
    var errors = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(HEADER + body).GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.ToString())
      .ToArray();
    await Assert.That(errors).IsEmpty();

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(HEADER + body);
    return [.. diagnostics.Where(d => d.Id == "WHIZ131")];
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RebuildCommand_NamingAnEphemeralOnlyPerspective_ReportsWHIZ131ErrorAsync() {
    var found = await _whiz131Async("""
      public static class Ops {
        public static RebuildPerspectiveCommand Make() => new(["TestApp.PresenceProjection"]);
      }
      """);

    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    var message = found[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("TestApp.PresenceProjection");
    await Assert.That(message).Contains("rebuild");
    await Assert.That(message).Contains("PresencePing");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RebuildCommand_ReportsAtTheNameNotTheCommandAsync() {
    var found = await _whiz131Async("""
      public static class Ops {
        public static RebuildPerspectiveCommand Make() => new(["TestApp.OrderProjection", "TestApp.PresenceProjection"]);
      }
      """);

    await Assert.That(found.Length).IsEqualTo(1);
    var text = (await found[0].Location.SourceTree!.GetTextAsync()).ToString(found[0].Location.SourceSpan);
    await Assert.That(text).IsEqualTo("\"TestApp.PresenceProjection\"");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RebuildCommand_ArrayCreationAndConstAndTypeofForms_EachReportWHIZ131Async() {
    var found = await _whiz131Async("""
      public static class Ops {
        private const string PRESENCE = "TestApp.PresenceProjection";
        public static RebuildPerspectiveCommand A() => new(new[] { PRESENCE });
        public static RebuildPerspectiveCommand B() => new(new string[] { typeof(PresenceProjection).FullName! });
        public static RebuildPerspectiveCommand C() => new(PerspectiveNames: [$"TestApp.{nameof(PresenceProjection)}"]);
        public static RebuildPerspectiveCommand D() => new([TypeNameFormatter.FormatClrTypeName(typeof(PresenceProjection))]);
      }
      """);

    await Assert.That(found.Length).IsEqualTo(4);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RebuildCommand_NamesSetInAnInitializer_ReportsWHIZ131Async() {
    var found = await _whiz131Async("""
      public static class Ops {
        public static RebuildPerspectiveCommand Make() =>
          new() { Mode = RebuildMode.InPlace, PerspectiveNames = ["TestApp.PresenceProjection"] };
      }
      """);

    await Assert.That(found.Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NestedPerspective_NamedWithItsClrName_ReportsWHIZ131Async() {
    var found = await _whiz131Async("""
      public static class Outer {
        public class Nested : IPerspectiveFor<Model, PresencePing> {
          public Model Apply(Model current, PresencePing e) => current;
        }
      }
      public static class Ops {
        public static RebuildPerspectiveCommand Make() => new(["TestApp.Outer+Nested"]);
      }
      """);

    await Assert.That(found.Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RebuilderCalls_NamingAnEphemeralOnlyPerspective_ReportWHIZ131Async() {
    var found = await _whiz131Async("""
      public static class Ops {
        public static async Task Run(IPerspectiveRebuilder rebuilder, Guid id) {
          await rebuilder.RebuildInPlaceAsync("TestApp.PresenceProjection");
          await rebuilder.RebuildBlueGreenAsync(typeof(PresenceProjection).FullName!, new RebuildOrigin());
          await rebuilder.RebuildStreamsAsync("TestApp.PresenceProjection", [id]);
        }
      }
      """);

    await Assert.That(found.Length).IsEqualTo(3);
    await Assert.That(found.All(d => d.GetMessage(CultureInfo.InvariantCulture).Contains("rebuild"))).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RebuilderImplementation_CalledDirectly_ReportsWHIZ131Async() {
    var found = await _whiz131Async("""
      public abstract class MyRebuilder : IPerspectiveRebuilder {
        public abstract Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default);
        public abstract Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default);
        public abstract Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, System.Collections.Generic.IEnumerable<Guid> streamIds, CancellationToken ct = default);
        public abstract Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default);
      }
      public static class Ops {
        public static Task Run(MyRebuilder rebuilder) => rebuilder.RebuildInPlaceAsync("TestApp.PresenceProjection");
      }
      """);

    await Assert.That(found.Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task RewindRequest_AgainstAnEphemeralOnlyPerspective_ReportsWHIZ131Async() {
    var found = await _whiz131Async("""
      public static class Ops {
        public static async Task Run(IPerspectiveRunner runner, Guid stream, Guid trigger) {
          await runner.RewindAndRunAsync(stream, "TestApp.PresenceProjection", trigger);
          await runner.RewindAndRunAsync(stream, typeof(PresenceProjection).FullName!, trigger, 42L);
        }
      }
      """);

    await Assert.That(found.Length).IsEqualTo(2);
    await Assert.That(found.All(d => d.GetMessage(CultureInfo.InvariantCulture).Contains("rewind"))).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MixedAndSourcedPerspectives_NoWHIZ131Async() {
    // A perspective that mixes modes is WHIZ130's to report; one that applies only Sourced events rebuilds fine.
    var found = await _whiz131Async("""
      public static class Ops {
        public static RebuildPerspectiveCommand Make() => new(["TestApp.MixedProjection", "TestApp.OrderProjection"]);
        public static Task Run(IPerspectiveRebuilder rebuilder) => rebuilder.RebuildInPlaceAsync("TestApp.MixedProjection");
      }
      """);

    await Assert.That(found.Length).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task NamesThatAreNotStaticallyAPerspective_NoWHIZ131Async() {
    // A runtime string, an unknown name, a type that is not a perspective, a simple name (not the name the registry
    // matches), the all-perspectives form and a non-string element are all left to the runtime guard.
    var found = await _whiz131Async("""
      public static class Ops {
        public static RebuildPerspectiveCommand A(string name) => new([name]);
        public static RebuildPerspectiveCommand B() => new(["TestApp.Unknown", "TestApp.Model", nameof(PresenceProjection)]);
        public static RebuildPerspectiveCommand C() => new();
        public static RebuildPerspectiveCommand D() => new(null, RebuildMode.InPlace);
        public static RebuildPerspectiveCommand E(string[] names) => new(names);
        public static RebuildPerspectiveCommand F() => new([typeof(PresenceProjection).Name]);
        public static RebuildPerspectiveCommand G() => new() { RequestedBy = "TestApp.PresenceProjection" };
        public static RebuildPerspectiveCommand H(Type type) => new([type.FullName!, TypeNameFormatter.FormatClrTypeName(type)]);
        public static RebuildPerspectiveCommand I() => new([typeof(PresenceProjection[]).FullName!, Name(), null!]);
        public static RebuildPerspectiveCommand J() => new(new string[1]);
        public static RebuildPerspectiveCommand K() => new([Lookalike.FormatClrTypeName(typeof(PresenceProjection))]);
        private static string Name() => "TestApp.PresenceProjection";
      }
      public static class Lookalike {
        public static string FormatClrTypeName(Type type) => "TestApp.PresenceProjection";
      }
      """);

    await Assert.That(found.Length).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task InitializerNamingAMemberThatDoesNotBind_NoWHIZ131Async() {
    // An analyzer also runs over code that does not compile yet, in the IDE. An initializer entry the compiler could
    // not bind is not the names property, whatever its value says.
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(HEADER + """
      public static class Ops {
        public static RebuildPerspectiveCommand Make() => new() { Perspectives = ["TestApp.PresenceProjection"] };
      }
      """);

    await Assert.That(diagnostics.Any(d => d.Id == "WHIZ131")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CallsThatAreNotARebuildOrRewind_NoWHIZ131Async() {
    // Reading a rebuild's status, a lookalike method on an unrelated type, and a runner's ordinary run name the
    // perspective without asking to replay it.
    var found = await _whiz131Async("""
      public class Lookalike {
        public Task RebuildInPlaceAsync(string perspectiveName) => Task.CompletedTask;
        public Task RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId) => Task.CompletedTask;
      }
      public static class Ops {
        public static async Task Run(IPerspectiveRebuilder rebuilder, IPerspectiveRunner runner, Lookalike other, Guid id) {
          await rebuilder.GetRebuildStatusAsync("TestApp.PresenceProjection");
          await other.RebuildInPlaceAsync("TestApp.PresenceProjection");
          await other.RewindAndRunAsync(id, "TestApp.PresenceProjection", id);
          await runner.RunAsync(id, "TestApp.PresenceProjection", null);
          Log("TestApp.PresenceProjection");
        }
        private static void Log(string text) { }
      }
      """);

    await Assert.That(found.Length).IsEqualTo(0);
  }
}
