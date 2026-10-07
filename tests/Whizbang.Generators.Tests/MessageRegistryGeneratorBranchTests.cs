// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The message registry's classification of a message it only sees used, because the type is
/// declared in a referenced assembly, and its refusal of a same-named call that is not a dispatcher.
/// </summary>
/// <tests>src/Whizbang.Generators/MessageRegistryGenerator.cs</tests>
[Category("SourceGenerators")]
public partial class MessageRegistryGeneratorBranchTests {
  [GeneratedRegex(@"""+type""+:\s*""+(?<type>[^""]+)""+,\s*""+isCommand""+:\s*(?<command>true|false),\s*""+isEvent""+:\s*(?<event>true|false)")]
  private static partial Regex _messageHeader();

  private static Dictionary<string, (bool IsCommand, bool IsEvent)> _classified(string registry) =>
    _messageHeader().Matches(registry).ToDictionary(
      m => m.Groups["type"].Value,
      m => (m.Groups["command"].Value == "true", m.Groups["event"].Value == "true"),
      StringComparer.Ordinal);

  /// <summary>
  /// A message declared in a referenced assembly has no declaration here to classify, so the
  /// registry infers it from how this assembly uses it: handled by a receptor is a command, applied
  /// by a perspective is an event, and only ever dispatched is an event.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ReferencedMessages_AreClassifiedByUseAsync() {
    var library = GeneratorTestHelper.CreateCompilation("""
      using Whizbang.Core;
      namespace Lib;
      public record ExtCommand(string Id) : ICommand;
      public record ExtEvent(string Id) : IEvent;
      public record ExtNotice(string Id) : IEvent;
      """, "Lib");
    var app = GeneratorTestHelper.CreateCompilation("""
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace App;

      public record LocalDone(string Id) : IEvent;

      public class ExtCommandReceptor : IReceptor<Lib.ExtCommand, LocalDone> {
        public ValueTask<LocalDone> HandleAsync(Lib.ExtCommand message, CancellationToken cancellationToken = default) =>
          ValueTask.FromResult(new LocalDone(message.Id));
      }

      public class Model { public string Id { get; set; } = ""; }

      public class ExtEventPerspective : IPerspectiveFor<Model, Lib.ExtEvent> {
        public Model Apply(Model currentData, Lib.ExtEvent @event) => currentData;
      }

      public class Notifier {
        public Task NotifyAsync(IDispatcher dispatcher) => dispatcher.PublishAsync(new Lib.ExtNotice("n"));
      }
      """, "App").AddReferences(library.ToMetadataReference());

    var result = CSharpGeneratorDriver.Create(new MessageRegistryGenerator()).RunGenerators(app).GetRunResult();
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs") ?? "";
    var classified = _classified(registry);

    await Assert.That(classified).ContainsKey("global::Lib.ExtCommand");
    await Assert.That(classified["global::Lib.ExtCommand"]).IsEqualTo((true, false));
    await Assert.That(classified["global::Lib.ExtEvent"]).IsEqualTo((false, true));
    await Assert.That(classified["global::Lib.ExtNotice"]).IsEqualTo((false, true));
  }

  /// <summary>
  /// A call through a function-pointer member named <c>SendAsync</c> matches the registry's name
  /// filter, but it binds to the pointer's type rather than to a method, so it is not a dispatcher
  /// call site.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task FunctionPointerNamedSendAsync_IsNotADispatcherAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("""
      using Whizbang.Core;

      namespace App;

      public record Ping(string Id) : ICommand;

      public unsafe class Sender {
        public delegate*<object, void> SendAsync;

        public void Run() {
          this.SendAsync(new Ping("p"));
        }
      }
      """, "App").WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    var result = CSharpGeneratorDriver.Create(new MessageRegistryGenerator()).RunGenerators(compilation).GetRunResult();
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs") ?? "";

    await Assert.That(registry).Contains("global::App.Ping")
      .Because("the control: the message itself is still registered");
    await Assert.That(registry).DoesNotContain("App.Sender");
  }
}
