using Whizbang.Core.Tags;

namespace Whizbang.Core.Priority;

/// <summary>
/// The producer sugar (priority step 2): declares the priority bound to a message type's tag through
/// <see cref="TagOptions.DeclarePriority(string, int)"/>. Runs before the framework's context default (Order 500),
/// keeps an explicit declaration made earlier, and leaves untagged types for the default to derive. The
/// tag-to-type index is built once from the registered tag registries and keyed by the type name the shared
/// helper renders, so the cascade path (whose payload is already storage-form JSON) resolves by name too.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#declaring-with-tags</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/PriorityTagSurfaceTests.cs</tests>
public sealed class TagDeclaredPriorityProducerHook : IPriorityProducerHook {
  private readonly TagOptions _tagOptions;
  private readonly Lazy<Dictionary<string, int>> _byTypeName;

  /// <summary>Creates the hook over the host's tag options.</summary>
  public TagDeclaredPriorityProducerHook(TagOptions tagOptions) {
    _tagOptions = tagOptions ?? throw new ArgumentNullException(nameof(tagOptions));
    // Lazy and thread-safe: built on first use, after module initializers have populated the tag registries and
    // the host has finished composing its declarations.
    _byTypeName = new(_buildIndex, LazyThreadSafetyMode.ExecutionAndPublication);
  }

  /// <inheritdoc />
  public int Order => 500;

  /// <inheritdoc />
  public int DeclarePriority(PriorityDeclarationContext context) {
    ArgumentNullException.ThrowIfNull(context);
    if (WorkPriority.IsDeclared(context.Declared)) {
      return context.Declared;
    }
    return _byTypeName.Value.TryGetValue(context.MessageTypeName, out var declared) ? declared : context.Declared;
  }

  private Dictionary<string, int> _buildIndex() {
    var index = new Dictionary<string, int>(StringComparer.Ordinal);
    if (_tagOptions.PriorityDeclarations.Count == 0) {
      return index;
    }
    foreach (var registration in MessageTagRegistry.GetAllTags()) {
      if (!_tagOptions.PriorityDeclarations.TryGetValue(registration.Tag, out var declared)) {
        continue;
      }
      var typeName = TypeNameFormatter.AssemblyQualifiedNameOrNull(registration.MessageType) ?? TypeNameFormatter.DisplayName(registration.MessageType);
      // A type carrying two declared tags takes the more urgent declaration.
      index[typeName] = index.TryGetValue(typeName, out var existing) ? Math.Min(existing, declared) : declared;
    }
    return index;
  }
}

/// <summary>
/// The consumer sugar (priority step 2): classifies a received message by the rules in <see cref="PriorityOptions"/>.
/// The most specific rule wins: a type rule, then the longest matching namespace (on a segment boundary), then the
/// predicates in registration order. Runs before the framework's accept-declared default (Order 500); a message
/// no rule matches keeps its declared number, so classification stays positive.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#declaring-with-tags</docs>
/// <tests>tests/Whizbang.Core.Tests/Priority/PriorityTagSurfaceTests.cs</tests>
public sealed class PriorityClassificationReceiveHook(PriorityOptions options) : IPriorityReceiveHook {
  private readonly PriorityOptions _options = options ?? throw new ArgumentNullException(nameof(options));

  /// <inheritdoc />
  public int Order => 500;

  /// <inheritdoc />
  public int Classify(PriorityReceiveContext context) {
    ArgumentNullException.ThrowIfNull(context);
    if (_options.TypeRules.TryGetValue(context.MessageTypeName, out var byType)) {
      return byType;
    }
    var typeFullName = TypeNameFormatter.GetFullName(context.MessageTypeName);
    foreach (var (ns, priority) in _options.NamespaceRules) {
      if (typeFullName.Length > ns.Length
          && typeFullName.StartsWith(ns, StringComparison.Ordinal)
          && typeFullName[ns.Length] == '.') {
        return priority;
      }
    }
    foreach (var rule in _options.Rules) {
      if (rule(context) is { } answer) {
        return answer;
      }
    }
    return context.Declared;
  }
}
