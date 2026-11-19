// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The subscribed event namespaces include those of events a synchronous receptor, a notification tag or the collective
/// sink consumes (#2065), and only those: a type that is not a concrete, namespaced event adds nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/EventNamespaceRegistryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class EventNamespaceRegistryGeneratorConsumerKindsTests {

  private const string ATTRIBUTES = """
namespace Whizbang.Core {
  public sealed class NotificationTagAttribute : System.Attribute {
    public NotificationTagAttribute(string tag) { }
  }
  public sealed class NotificationIdTagAttribute : System.Attribute {
    public NotificationIdTagAttribute(string tag) { }
  }
}
""";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TypesThatAreNotConcreteNamespacedConsumedEvents_AddNoNamespaceAsync() {
    const string source = ATTRIBUTES + """
namespace Skip.AbstractTagged {
  [Whizbang.Core.NotificationTagAttribute("t")]
  public abstract record AbstractTagged : Whizbang.Core.IEvent;
}
namespace Skip.TaggedNonEvent {
  [Whizbang.Core.NotificationTagAttribute("t")]
  public record TaggedNotice;
}
namespace Skip.UntaggedAttributed {
  [System.Serializable]
  public record Plain : Whizbang.Core.IEvent;
}
namespace Skip.NotCollective {
  public record JustAnEvent : Whizbang.Core.IEvent;
}
namespace Skip.SyncCommand {
  public record DoIt : Whizbang.Core.ICommand;
  public class DoItReceptor : Whizbang.Core.ISyncReceptor<DoIt> { public void Handle(DoIt message) { } }
}
[Whizbang.Core.NotificationTagAttribute("t")]
public record GlobalTagged : Whizbang.Core.IEvent;
""";

    var subscribed = _subscribed(source);

    await Assert.That(subscribed.Where(ns => ns.StartsWith("skip.", StringComparison.Ordinal))).IsEmpty()
      .Because("an abstract type, a tagged non-event, an untagged event, a non-collective event and a command a "
             + "synchronous receptor handles are not consumed events with a subscription of their own");
    await Assert.That(subscribed).DoesNotContain("<global namespace>");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TaggedSyncAndCollectiveEvents_AreSubscribedAsReceptorNamespacesAsync() {
    const string source = ATTRIBUTES + """
namespace Take.Tagged {
  [Whizbang.Core.NotificationIdTagAttribute("t")]
  public record Tagged : Whizbang.Core.IEvent;
}
namespace Take.Synced {
  public record Synced : Whizbang.Core.IEvent;
  public class SyncedReceptor : Whizbang.Core.ISyncReceptor<Synced, int> { public int Handle(Synced message) => 0; }
}
namespace Take.Swept {
  public sealed record Swept(Whizbang.Core.Messaging.CollectiveScope Scope,
    System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : Whizbang.Core.Messaging.ICollectiveEvent;
}
""";

    var generated = _generated(source);
    var start = generated.IndexOf("_receptorNamespaces", StringComparison.Ordinal);
    var receptorRegion = generated[start..generated.IndexOf("};", start, StringComparison.Ordinal)];

    await Assert.That(receptorRegion).Contains("\"take.tagged\"");
    await Assert.That(receptorRegion).Contains("\"take.synced\"");
    await Assert.That(receptorRegion).Contains("\"take.swept\"");
  }

  private static string _generated(string source) {
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs") ?? throw new InvalidOperationException(
      "EventNamespaceSource.g.cs was not generated: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
  }

  private static List<string> _subscribed(string source) {
    var generated = _generated(source);
    var start = generated.IndexOf("_allNamespaces", StringComparison.Ordinal);
    var region = generated[start..generated.IndexOf("};", start, StringComparison.Ordinal)];
    return [.. region.Split('"').Where((_, i) => i % 2 == 1)];
  }
}
