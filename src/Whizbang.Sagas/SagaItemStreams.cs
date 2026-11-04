// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Sagas;

/// <summary>
/// Derives a deterministic per-item stream id from
/// <c>(sagaId, itemIdentifier)</c> using an RFC 4122 v5 UUID over a
/// configurable namespace.
/// </summary>
/// <remarks>
/// <para>
/// Per-item saga events
/// (<see cref="ISagaItemStartedEvent"/>, <see cref="ISagaItemCompletedEvent"/>,
/// <see cref="ISagaItemFailedEvent"/>) route to a stream keyed by item,
/// not by saga, so concurrent items don't contend for the saga's stream
/// lease — eliminating the cross-pod lost-update race the
/// saga-as-single-aggregate design carried.
/// </para>
/// <para>
/// The derivation is byte-for-byte reproducible in SQL via the
/// canonical <c>saga_item_stream_id(uuid, text)</c> function (shipped
/// by consumers as needed). Both compute SHA-1 over
/// <c>{namespace_bytes || utf8("{sagaId:D}/{itemIdentifier}")}</c> then
/// stamp version 5 and the RFC 4122 variant — identical big-endian
/// formatting on both sides.
/// </para>
/// <para>
/// <strong>Namespace stability is load-bearing.</strong> Every per-item
/// stream id ever persisted in a consumer's production data was derived
/// from a specific namespace UUID. Changing the namespace re-routes
/// every item to a new stream and orphans the existing projection rows.
/// Consumers migrating from an existing system (e.g. with their own
/// pre-Whizbang namespace) supply that namespace via
/// <c>[Saga(PerItemNamespaceHex = "…")]</c>; fresh consumers use the
/// Whizbang default.
/// </para>
/// </remarks>
public static class SagaItemStreams {

  /// <summary>
  /// Whizbang's stable factory-default namespace UUID. Used when neither
  /// <see cref="AppDefaultNamespace"/> has been configured nor an explicit
  /// override has been passed to <see cref="Of(Guid, Guid, string)"/>.
  /// <strong>Never change this value</strong> — every fresh consumer's
  /// persisted stream ids derive from it.
  /// </summary>
  public static readonly Guid DefaultNamespace = Guid.Parse("acb2e0cf-92d5-4f1b-8c5e-2d7f4a5e8b3a");

  /// <summary>
  /// The namespace UUID currently in effect for the
  /// <see cref="Of(Guid, string)"/> no-override path: the
  /// <c>SagaOptions.PerItemStreamNamespace</c> of the first <c>AddWhizbangSagas</c>
  /// call in the process; <see cref="DefaultNamespace"/> if none has run.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <strong>Process-wide, first registration wins (#2021).</strong> One value for the whole
  /// process. With several hosts in one process (integration tests, a test server per service)
  /// the first <c>AddWhizbangSagas</c> call decides it; a later call with a different namespace
  /// leaves it unchanged and its host logs a warning at start naming both namespaces, and a
  /// later call with the same namespace is silent. A value cannot change between two reads once
  /// set. The framework's own readers do not use it: they derive from their host's options
  /// through <see cref="ISagaItemStreamIdDeriver"/>, which code running in a host should inject
  /// as well.
  /// </para>
  /// <para>
  /// <strong>Configure before any saga operation runs.</strong> The namespace is stream identity:
  /// every per-item stream id derives from it.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/sagas/whizbang-sagas#per-item-streams-cross-pod-safe-fan-out</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamsFirstRegistrationWinsTests.cs</tests>
  public static Guid AppDefaultNamespace {
    get => _registration.Current ?? DefaultNamespace;
    // Tests only: overwrites whatever is registered. Registrations go through RegisterAppDefaultNamespace.
    internal set => _registration.Overwrite(value);
  }

  /// <summary>The process-wide registration: first wins, atomically.</summary>
  private static readonly FirstRegistrationSlot _registration = new();

  /// <summary>
  /// Registers <paramref name="namespaceId"/> as the process-wide namespace unless one is registered already.
  /// </summary>
  /// <param name="namespaceId">The namespace a registration asks for.</param>
  /// <returns>The namespace in effect afterward: <paramref name="namespaceId"/> for the first registration, the first
  /// registration's namespace for every later one.</returns>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamsFirstRegistrationWinsTests.cs:ALaterRegistrationWithADifferentNamespace_DoesNotOverwriteTheFirstAsync</tests>
  internal static Guid RegisterAppDefaultNamespace(Guid namespaceId) => _registration.Register(namespaceId);

  /// <summary>
  /// Clears the registration so the next one is the first; for tests that share this process-wide value. Disposing the
  /// returned scope puts back exactly what was there, including no registration at all.
  /// </summary>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamsFirstRegistrationWinsTests.cs:ResetForTesting_DisposingTheScope_PutsBackTheRegistrationItClearedAsync</tests>
  internal static IDisposable ResetAppDefaultNamespaceForTesting() => _registration.Reset();

  /// <summary>
  /// Derives the per-item stream id for <paramref name="sagaId"/> /
  /// <paramref name="itemIdentifier"/> using <see cref="AppDefaultNamespace"/>
  /// (which equals <see cref="DefaultNamespace"/> unless overridden via
  /// <c>AddWhizbangSagas(opts =&gt; opts.PerItemStreamNamespace = …)</c>).
  /// </summary>
  /// <remarks>
  /// For application code with no container. The namespace is process-wide,
  /// so in a process with several hosts it is whichever the first
  /// <c>AddWhizbangSagas</c> call set; code running in a host injects
  /// <see cref="ISagaItemStreamIdDeriver"/>, which derives under that host's
  /// own namespace.
  /// </remarks>
  /// <docs>fundamentals/sagas/whizbang-sagas#per-item-streams-cross-pod-safe-fan-out</docs>
  public static Guid Of(Guid sagaId, string itemIdentifier) =>
    Of(AppDefaultNamespace, sagaId, itemIdentifier);

  /// <summary>
  /// Derives the per-item stream id for <paramref name="sagaId"/> /
  /// <paramref name="itemIdentifier"/> using the explicit
  /// <paramref name="namespaceId"/>. Use this overload when a consumer's
  /// historical persisted stream ids were derived from a different
  /// namespace.
  /// </summary>
  /// <param name="namespaceId">RFC 4122 v5 namespace UUID.</param>
  /// <param name="sagaId">Owning saga's stream id.</param>
  /// <param name="itemIdentifier">Caller-supplied item identifier. Must be non-empty.</param>
  /// <returns>An RFC 4122 v5 UUID derived from the namespace and <c>{sagaId:D}/{itemIdentifier}</c>.</returns>
  public static Guid Of(Guid namespaceId, Guid sagaId, string itemIdentifier) {
    ArgumentException.ThrowIfNullOrWhiteSpace(itemIdentifier);

    var namespaceBytes = namespaceId.ToByteArray(bigEndian: true);
    var nameBytes = Encoding.UTF8.GetBytes($"{sagaId:D}/{itemIdentifier}");

    var input = new byte[namespaceBytes.Length + nameBytes.Length];
    namespaceBytes.CopyTo(input, 0);
    nameBytes.CopyTo(input, namespaceBytes.Length);

    Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350, S4790 // RFC 4122 v5 mandates SHA-1; this is deterministic identity derivation, not authentication/security.
    SHA1.HashData(input, hash);
#pragma warning restore CA5350, S4790

    Span<byte> uuid = hash[..16];
    uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);   // version 5
    uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);   // variant 10xx (RFC 4122)

    return new Guid(uuid, bigEndian: true);
  }
}
