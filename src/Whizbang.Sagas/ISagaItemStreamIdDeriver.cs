// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>
/// Derives per-item saga stream ids under one host's configured namespace.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddWhizbangSagas</c> registers one per container, built from that container's own
/// <see cref="SagaOptions.PerItemStreamNamespace"/>. Inject it wherever per-item stream ids are derived in code that
/// runs inside a host (an item event's stream id, a terminal-event lookup), so every host in a process derives under
/// its own namespace however many hosts the process builds.
/// </para>
/// <para>
/// <see cref="SagaItemStreams.Of(Guid, string)"/> is the process-wide equivalent for code with no container: it reads
/// one value that every <c>AddWhizbangSagas</c> call in the process overwrites, so with several hosts in one process the
/// last registration decides it for all of them.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/whizbang-sagas#per-item-streams-cross-pod-safe-fan-out</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamIdDeriverTests.cs</tests>
/// <tests>tests/Whizbang.Sagas.Component.Tests/SagaItemStreamNamespacePerHostTests.cs</tests>
public interface ISagaItemStreamIdDeriver {
  /// <summary>The namespace UUID this deriver derives under.</summary>
  Guid NamespaceId { get; }

  /// <summary>
  /// The per-item stream id for <paramref name="sagaId"/> / <paramref name="itemIdentifier"/> under
  /// <see cref="NamespaceId"/>, exactly as <see cref="SagaItemStreams.Of(Guid, Guid, string)"/> derives it.
  /// </summary>
  /// <param name="sagaId">Owning saga's stream id.</param>
  /// <param name="itemIdentifier">Caller-supplied item identifier. Must be non-empty.</param>
  /// <returns>An RFC 4122 v5 UUID derived from <see cref="NamespaceId"/> and <c>{sagaId:D}/{itemIdentifier}</c>.</returns>
  Guid StreamIdOf(Guid sagaId, string itemIdentifier);
}
