// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;

namespace Whizbang.Generators.Tests.BuildCost;

/// <summary>
/// A synthetic consumer codebase shaped like a real service: commands, events, receptors, read models and
/// perspectives, plus one file of ordinary code that no generator should care about. The build-cost tests run
/// every generator over it, so its shape decides what they measure; keep it close to what consumers write.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
public static class ConsumerCorpus {
  /// <summary>The path of the file of ordinary code, the target of the unrelated edits.</summary>
  public const string UNRELATED_FILE = "Unrelated/Calculator.cs";

  /// <summary>The path of the first receptor, the target of the receptor body edit.</summary>
  public const string FIRST_RECEPTOR_FILE = "Feature0/Receptors.cs";

  /// <summary>
  /// Builds the corpus as (path, source) pairs: <paramref name="features"/> features of one command, one event,
  /// one receptor, one read model and one perspective each, plus the file of ordinary code.
  /// </summary>
  public static IReadOnlyList<(string Path, string Source)> Build(int features) {
    var files = new List<(string, string)>(features * 3 + 1);
    for (var i = 0; i < features; i++) {
      files.Add(($"Feature{i}/Messages.cs", _messages(i)));
      files.Add(($"Feature{i}/Receptors.cs", _receptor(i, "\"created\"")));
      files.Add(($"Feature{i}/Perspectives.cs", _perspective(i)));
    }
    files.Add((UNRELATED_FILE, Unrelated(1)));
    return files;
  }

  /// <summary>The file of ordinary code, with <paramref name="factor"/> as the only thing an edit changes.</summary>
  public static string Unrelated(int factor) => $$"""
    namespace Consumer.Unrelated;

    public static class Calculator {
      public static int Scale(int value) => value * {{factor}};
    }
    """;

  /// <summary>The first receptor, with <paramref name="literal"/> as the only thing an edit changes.</summary>
  public static string FirstReceptor(string literal) => _receptor(0, literal);

  private static string _messages(int i) => $$"""
    using System;
    using Whizbang.Core;

    namespace Consumer.Feature{{i}};

    public record CreateThing{{i}}Command : ICommand {
      public required Guid ThingId { get; init; }
      public required string Name { get; init; }
    }

    public record Thing{{i}}CreatedEvent : IEvent {
      [StreamId]
      public required Guid ThingId { get; init; }
      public required string Name { get; init; }
      public DateTime CreatedAt { get; init; }
    }
    """;

  private static string _receptor(int i, string literal) => $$"""
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;

    namespace Consumer.Feature{{i}};

    public class CreateThing{{i}}Receptor : IReceptor<CreateThing{{i}}Command, Thing{{i}}CreatedEvent> {
      public ValueTask<Thing{{i}}CreatedEvent> HandleAsync(CreateThing{{i}}Command message, CancellationToken cancellationToken = default) {
        var note = {{literal}};
        return ValueTask.FromResult(new Thing{{i}}CreatedEvent { ThingId = message.ThingId, Name = message.Name + note });
      }
    }
    """;

  private static string _perspective(int i) => $$"""
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace Consumer.Feature{{i}};

    public record Thing{{i}}View {
      [StreamId]
      public Guid ThingId { get; init; }
      public string Name { get; init; } = "";
    }

    public class Thing{{i}}Perspective : IPerspectiveFor<Thing{{i}}View, Thing{{i}}CreatedEvent> {
      public Thing{{i}}View Apply(Thing{{i}}View currentData, Thing{{i}}CreatedEvent @event) =>
        new() { ThingId = @event.ThingId, Name = @event.Name };
    }
    """;

  /// <summary>Joins the corpus into one string, for a quick look when a test fails.</summary>
  public static string Describe(IReadOnlyList<(string Path, string Source)> files) {
    var sb = new StringBuilder();
    foreach (var (path, _) in files) {
      sb.AppendLine(path);
    }
    return sb.ToString();
  }
}
