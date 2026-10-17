// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.ComponentModel;

namespace Whizbang.Core.Dispatch;

/// <summary>
/// The <see cref="DefaultRoutingAttribute"/> of each message type, registered by every assembly's
/// generated message catalog when the assembly loads. The cascade reads it here instead of through
/// reflection, so it works the same under native AOT.
/// </summary>
/// <docs>fundamentals/dispatcher/message-cascade#routed-message-cascading</docs>
public static class MessageDefaultRouting {
  private static readonly ConcurrentDictionary<Type, DispatchModes> _byType = new();

  /// <summary>
  /// Registers a message type's routing. Called by generated code; the first registration of a type
  /// wins, as in the catalog union.
  /// </summary>
  /// <param name="messageType">The message type.</param>
  /// <param name="mode">Its <see cref="DefaultRoutingAttribute.Mode"/>.</param>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static void Register(Type messageType, DispatchModes mode) {
    ArgumentNullException.ThrowIfNull(messageType);
    _byType.TryAdd(messageType, mode);
  }

  /// <summary>The message type's registered routing, or null when it has no <see cref="DefaultRoutingAttribute"/>.</summary>
  /// <param name="messageType">The message type.</param>
  /// <returns>The routing, or null.</returns>
  public static DispatchModes? Find(Type messageType) {
    ArgumentNullException.ThrowIfNull(messageType);
    return _byType.TryGetValue(messageType, out var mode) ? mode : null;
  }
}
