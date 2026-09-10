namespace Whizbang.Core.Priority;

/// <summary>
/// The consumer-side classification rules (priority step 2): sugar over <see cref="IPriorityReceiveHook"/>. A rule
/// decides the effective priority of a received message by its namespace, its type, or a predicate over the
/// receive context; a rule that does not match has no opinion, so the declared number stands (classification is
/// positive). The most specific rule wins: a type rule over a namespace rule over a predicate. Applied by
/// <see cref="PriorityClassificationReceiveHook"/>, which runs before the framework's accept-declared default.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#declaring-with-tags</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/PriorityTagSurfaceTests.cs</tests>
public sealed class PriorityOptions {
  private readonly Dictionary<string, int> _byType = new(StringComparer.Ordinal);
  private readonly List<(string Namespace, int Priority)> _byNamespace = [];
  private readonly List<Func<PriorityReceiveContext, int?>> _rules = [];

  /// <summary>Rules by type, keyed by the type name the shared helper renders.</summary>
  public IReadOnlyDictionary<string, int> TypeRules => _byType;

  /// <summary>Rules by namespace, longest namespace first.</summary>
  public IReadOnlyList<(string Namespace, int Priority)> NamespaceRules => _byNamespace;

  /// <summary>Predicate rules in registration order.</summary>
  public IReadOnlyList<Func<PriorityReceiveContext, int?>> Rules => _rules;

  /// <summary>Every message whose type lives in <paramref name="namespace"/> (or a namespace under it) gets <paramref name="priority"/>.</summary>
  public PriorityOptions ClassifyNamespace(string @namespace, int priority) {
    ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priority);
    _byNamespace.RemoveAll(r => string.Equals(r.Namespace, @namespace, StringComparison.Ordinal));
    _byNamespace.Add((@namespace, priority));
    _byNamespace.Sort((a, b) => b.Namespace.Length.CompareTo(a.Namespace.Length));
    return this;
  }

  /// <summary>Every message of type <typeparamref name="TMessage"/> gets <paramref name="priority"/>; wins over a namespace rule.</summary>
  public PriorityOptions ClassifyType<TMessage>(int priority) where TMessage : notnull {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priority);
    _byType[TypeNameFormatter.AssemblyQualifiedName(typeof(TMessage))] = priority;
    return this;
  }

  /// <summary>A rule over the receive context; returning null keeps the declared number.</summary>
  public PriorityOptions Classify(Func<PriorityReceiveContext, int?> rule) {
    ArgumentNullException.ThrowIfNull(rule);
    _rules.Add(rule);
    return this;
  }
}
