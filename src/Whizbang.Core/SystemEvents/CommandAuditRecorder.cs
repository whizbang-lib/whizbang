// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Emits one <see cref="CommandAudited"/> for every receptor admitted to handle a command (#2470). It runs at the gate
/// every receptor invocation passes (local invoke, send, cascade, inbox delivery and every other lifecycle stage), after
/// every receptor interceptor allowed, so no path handles a command without it and a denied receptor is not audited.
/// </summary>
/// <remarks>
/// <para>
/// A command is a message that implements <see cref="ICommand"/>, the framework's one definition of the kind. A type
/// with <c>[AuditEvent(Exclude = true)]</c> is not audited. The record is written when the receptor is admitted, before
/// it runs: it records what was requested, so a command whose receptor then fails is still in the trail. For the same
/// reason it names no response type.
/// </para>
/// <para>
/// A synchronous receptor runs on the caller's thread and cannot wait for the store, so its record is emitted
/// fire-and-forget through <see cref="SystemEventFireAndForget"/>, which logs and counts a record that fails to store.
/// Every other receptor waits for its record, so a store that refuses it fails the invocation.
/// </para>
/// <para>
/// Registered by <see cref="SystemEventServiceCollectionExtensions.AddSystemEvents"/> only when command audit is
/// enabled, so a host without command audit has no recorder and pays nothing per message.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/audit-logging#command-auditing</docs>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/CommandAuditDispatchTests.cs</tests>
internal sealed class CommandAuditRecorder(IServiceProvider rootProvider, ILogger<CommandAuditRecorder> logger) : IReceptorAdmissionRecorder {
  private readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();

  /// <inheritdoc />
  public ValueTask RecordAsync(Type receptorType, IMessageEnvelope envelope, IScopeContext? context, IServiceProvider? scopedProvider,
      CancellationToken cancellationToken) =>
    _recordFor(receptorType, envelope, context, scopedProvider ?? rootProvider) is { } pending
      ? new ValueTask(pending.Emitter.EmitAsync(pending.Record, cancellationToken))
      : ValueTask.CompletedTask;

  /// <inheritdoc />
  public void Record(Type receptorType, IMessageEnvelope envelope, IScopeContext? context, IServiceProvider scopedProvider) {
    if (_recordFor(receptorType, envelope, context, scopedProvider) is { } pending) {
      SystemEventFireAndForget.Emit(pending.Emitter, pending.Record, pending.Record.TenantId, logger,
        scopedProvider.GetService<EventCategoryMetrics>());
    }
  }

  /// <summary>The record to emit, with the emitter to emit it through; null when this invocation is not audited.</summary>
  private (ISystemEventEmitter Emitter, CommandAudited Record)? _recordFor(Type receptorType, IMessageEnvelope envelope,
      IScopeContext? context, IServiceProvider provider) {
    if (envelope.Payload is not ICommand command) {
      return null;
    }
    // Registered only when command audit was on at registration; the options resolved are the ones in force
    if (!provider.GetRequiredService<IOptions<SystemEventOptions>>().Value.CommandAuditEnabled) {
      return null;
    }
    var emitter = provider.GetRequiredService<ISystemEventEmitter>();
    var commandType = command.GetType();
    if (emitter.ShouldExcludeFromAudit(commandType)) {
      return null;
    }

    var tenantId = context?.Scope.TenantId;
    var userId = context?.Scope.UserId;
    var correlationId = envelope.GetCorrelationId()?.ToString();
    var scope = new Dictionary<string, string?>();
    if (tenantId is not null) {
      scope["TenantId"] = tenantId;
    }
    if (userId is not null) {
      scope["UserId"] = userId;
    }
    if (correlationId is not null) {
      scope["CorrelationId"] = correlationId;
    }
    var body = AuditJsonSerializer.SerializeToJsonElement<object>(command, _jsonOptions, logger);
    var record = CommandAuditRecord.Create(new CommandAuditRecord.Parts(
      command, body, TypeNameFormatter.SimpleName(commandType), TypeNameFormatter.SimpleName(receptorType),
      ResponseType: null, scope, tenantId, userId, correlationId, envelope.GetCausationId()?.ToString()));
    return (emitter, record);
  }
}

/// <summary>
/// Builds a <see cref="CommandAudited"/> record: the one place a command audit's body projection, identity and scope
/// are assembled, shared by <see cref="ISystemEventEmitter.EmitCommandAuditedAsync"/> and
/// <see cref="CommandAuditRecorder"/>.
/// </summary>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/CommandAuditDispatchTests.cs</tests>
internal static class CommandAuditRecord {
  /// <summary>What a command audit record is made of.</summary>
  internal sealed record Parts(
    object Command,
    JsonElement Body,
    string CommandType,
    string ReceptorName,
    string? ResponseType,
    IReadOnlyDictionary<string, string?> Scope,
    string? TenantId,
    string? UserId,
    string? CorrelationId,
    string? CausationId);

  /// <summary>Creates the record.</summary>
  internal static CommandAudited Create(Parts parts) {
    var body = parts.Body;
    // A command that carries id lists of unbounded length keeps their lengths in the audit record, not the lists (#1525)
    if (parts.Command is Whizbang.Core.Audit.IBoundedCommandAudit bounded) {
      body = AuditBodyProjection.ListLengths(body, bounded.ListsAuditedAsLengths());
    }
    return new CommandAudited {
      Id = ValueObjects.TrackedGuid.New(),
      CommandType = parts.CommandType,
      CommandBody = body,
      Timestamp = DateTimeOffset.UtcNow,
      ReceptorName = parts.ReceptorName,
      ResponseType = parts.ResponseType,
      // Store individual properties for backward compatibility
      TenantId = parts.TenantId,
      UserId = parts.UserId,
      CorrelationId = parts.CorrelationId,
      CausationId = parts.CausationId,
      // Store full scope for generic access
      Scope = parts.Scope.Count > 0 ? parts.Scope : null
    };
  }
}
