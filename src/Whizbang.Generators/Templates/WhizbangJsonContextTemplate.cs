using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Transports;

#region NAMESPACE
namespace Whizbang.Core.Generated;
#endregion

#region HEADER
// This region will be replaced with auto-generated header
#endregion

#nullable enable

/// <summary>
/// Generated JsonSerializerContext with manual JsonTypeInfo objects for AOT-compatible serialization.
/// Implements IJsonTypeInfoResolver to properly handle options in resolver chains.
/// </summary>
public partial class MessageJsonContext : JsonSerializerContext, IJsonTypeInfoResolver {
  /// <summary>
  /// Default singleton instance of MessageJsonContext.
  /// Use this in resolver chains: MessageJsonContext.Default
  /// </summary>
  public static MessageJsonContext Default { get; } = new();

  public MessageJsonContext() : base(null) { }
  public MessageJsonContext(JsonSerializerOptions options) : base(options) { }

  protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

  #region LAZY_FIELDS
  // Lazy-initialized fields for all JsonTypeInfo objects
  #endregion

  #region LAZY_PROPERTIES
  // Lazy-initialized properties for all JsonTypeInfo objects
  #endregion

  #region ASSEMBLY_AWARE_HELPER
  // Assembly-aware helper method for creating JsonSerializerOptions with all contexts
  #endregion

  #region GET_DISCOVERED_TYPE_INFO
  // GetTypeInfo implementation for discovered message types (ICommand, IEvent)
  // The manually-written partial class calls this from its GetTypeInfo override
  #endregion

  #region HELPER_METHODS
  // Generic helper methods for creating JsonTypeInfo objects
  #endregion

  #region CORE_TYPE_FACTORIES
  // Factory methods for Whizbang core types (MessageId, CorrelationId, etc.)
  #endregion

  #region MESSAGE_TYPE_FACTORIES
  // Factory methods for discovered message types (ICommand, IEvent)
  #endregion

  #region MESSAGE_ENVELOPE_FACTORIES
  // Factory methods for MessageEnvelope<T> instances
  #endregion
}
