using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Whizbang.Core.Serialization;

/// <summary>
/// Recalls the correct <see cref="IVersionedJsonSerializer"/> for a stored blob's serialization
/// version, and exposes the <see cref="Current"/> (highest-version) serializer used to write new
/// blobs.
/// </summary>
/// <docs>event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/VersionedJsonSerializerRegistryTests.cs</tests>
public interface IVersionedJsonSerializerRegistry {
  /// <summary>The current (highest-version) serializer, used to write new blobs.</summary>
  IVersionedJsonSerializer Current { get; }

  /// <summary>Recalls the serializer registered for <paramref name="version"/>.</summary>
  /// <returns><c>true</c> when a serializer for that version is registered.</returns>
  bool TryGet(int version, [NotNullWhen(true)] out IVersionedJsonSerializer? serializer);
}

/// <summary>
/// Default <see cref="IVersionedJsonSerializerRegistry"/>: indexes registered serializers by
/// <see cref="IVersionedJsonSerializer.Version"/>. <see cref="Current"/> is the highest version.
/// AOT-safe — no reflection, serializers are supplied explicitly.
/// </summary>
/// <docs>event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/VersionedJsonSerializerRegistryTests.cs</tests>
public sealed class VersionedJsonSerializerRegistry : IVersionedJsonSerializerRegistry {
  private readonly Dictionary<int, IVersionedJsonSerializer> _byVersion;

  /// <summary>Builds the registry from the registered serializers.</summary>
  /// <param name="serializers">All registered versioned serializers (at least one).</param>
  /// <exception cref="ArgumentException">When no serializers are supplied, or two share a version.</exception>
  public VersionedJsonSerializerRegistry(IEnumerable<IVersionedJsonSerializer> serializers) {
    ArgumentNullException.ThrowIfNull(serializers);

    _byVersion = [];
    foreach (var serializer in serializers) {
      if (!_byVersion.TryAdd(serializer.Version, serializer)) {
        throw new ArgumentException(
          $"Two versioned serializers are registered for version {serializer.Version}.", nameof(serializers));
      }
    }

    if (_byVersion.Count == 0) {
      throw new ArgumentException("At least one versioned serializer must be registered.", nameof(serializers));
    }

    Current = _byVersion[_byVersion.Keys.Max()];
  }

  /// <inheritdoc />
  public IVersionedJsonSerializer Current { get; }

  /// <inheritdoc />
  public bool TryGet(int version, [NotNullWhen(true)] out IVersionedJsonSerializer? serializer) =>
    _byVersion.TryGetValue(version, out serializer);
}
