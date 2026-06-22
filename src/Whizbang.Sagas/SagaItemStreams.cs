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

  private static Guid _appDefaultNamespace = DefaultNamespace;

  /// <summary>
  /// The namespace UUID currently in effect for the
  /// <see cref="Of(Guid, string)"/> no-override path. Settable once at
  /// app startup via <c>SagaOptions.PerItemStreamNamespace</c>; defaults
  /// to <see cref="DefaultNamespace"/> if never set.
  /// </summary>
  /// <remarks>
  /// <strong>Configure before any saga operation runs.</strong> Changing
  /// this value at runtime re-derives every per-item stream id and
  /// orphans existing projection rows.
  /// </remarks>
  public static Guid AppDefaultNamespace {
    get => _appDefaultNamespace;
    internal set => _appDefaultNamespace = value;
  }

  /// <summary>
  /// Derives the per-item stream id for <paramref name="sagaId"/> /
  /// <paramref name="itemIdentifier"/> using <see cref="AppDefaultNamespace"/>
  /// (which equals <see cref="DefaultNamespace"/> unless overridden via
  /// <c>AddWhizbangSagas(opts =&gt; opts.PerItemStreamNamespace = …)</c>).
  /// </summary>
  public static Guid Of(Guid sagaId, string itemIdentifier) =>
    Of(_appDefaultNamespace, sagaId, itemIdentifier);

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
#pragma warning disable CA5350 // RFC 4122 v5 mandates SHA-1; this is identity derivation, not authentication.
    SHA1.HashData(input, hash);
#pragma warning restore CA5350

    Span<byte> uuid = hash[..16];
    uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);   // version 5
    uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);   // variant 10xx (RFC 4122)

    return new Guid(uuid, bigEndian: true);
  }
}
