namespace Whizbang.Core.Routing;

/// <summary>
/// The strategy-agnostic command-inbox address seam (topology arc phase 7): everything the
/// publish side needs to route commands — the default (shared) inbox address plus the
/// per-namespace flipped-resolution hook — without knowing WHICH outbox strategy is
/// registered. Implemented by both built-in command-routing strategies
/// (<see cref="SharedTopicOutboxStrategy"/>, which never flips, and
/// <see cref="NamespaceOutboxStrategy"/>, which consults the live flip set); the transports'
/// DI factories consume this interface instead of type-testing concrete strategies.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/CommandInboxAddressResolverTests.cs</tests>
public interface ICommandInboxAddressResolver {
  /// <summary>
  /// Gets the default command inbox address — the shared inbox topic commands publish to when
  /// their contract namespace is not flipped. This is the address
  /// <c>TransportPublishStrategy</c> falls back to for every command whose
  /// <see cref="ResolveFlippedCommandInboxAddress"/> answer is null.
  /// </summary>
  string DefaultCommandInboxAddress { get; }

  /// <summary>
  /// Resolves the flipped per-namespace inbox entity for a command contract namespace, or
  /// null when the namespace is not flipped (the caller keeps the legacy shared-inbox wire
  /// shape, byte-identical). Name-based on purpose: the publish-time caller only has the
  /// outbox row's type-name STRING, not the CLR type — AOT-safe by construction.
  /// </summary>
  /// <param name="contractNamespace">The command's contract namespace (any casing); null or
  /// empty resolves to null.</param>
  /// <returns>The flipped inbox entity name, or null to keep the default address.</returns>
  string? ResolveFlippedCommandInboxAddress(string? contractNamespace);
}

/// <summary>
/// The resolver registered when the active outbox strategy does not resolve command inbox
/// addresses (the domain-topic strategy does not). Flipping is unavailable, reported as a null
/// resolution exactly as a null resolver was; the default address is the shared inbox the
/// namespace strategy also defaults to.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
public sealed class NullCommandInboxAddressResolver : ICommandInboxAddressResolver, INullDefault {
  /// <summary>The shared instance; the type carries no state.</summary>
  public static NullCommandInboxAddressResolver Instance { get; } = new();
  private NullCommandInboxAddressResolver() { }
  /// <inheritdoc />
  public string DefaultCommandInboxAddress => SharedTopicOutboxStrategy.DefaultInboxTopic;
  /// <inheritdoc />
  public string? ResolveFlippedCommandInboxAddress(string? contractNamespace) => null;
}
