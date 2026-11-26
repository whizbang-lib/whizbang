// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Something the framework records for every receptor invocation the receptor interceptors admitted, on every path a
/// receptor runs (local dispatch, cascade, inbox delivery and every other lifecycle stage), because it is recorded where
/// the gate admits it. Command auditing is one (#2470).
/// </summary>
/// <remarks>
/// <para>
/// A recorder is not an <see cref="IReceptorInterceptor"/>: it never decides, and it runs only after every interceptor
/// allowed, whatever the registration order, so a denied receptor is never recorded.
/// </para>
/// <para>
/// A synchronous receptor runs on the caller's thread and cannot wait, so it is recorded with <see cref="Record"/>,
/// which must not block; every other receptor is recorded with <see cref="RecordAsync"/>, which is awaited before the
/// receptor runs.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/audit-logging#command-auditing</docs>
/// <tests>tests/Whizbang.Core.Unit.Dispatcher.Tests/SystemEvents/CommandAuditDispatchTests.cs</tests>
internal interface IReceptorAdmissionRecorder {
  /// <summary>Records that <paramref name="receptorType"/> was admitted to handle the message.</summary>
  /// <param name="receptorType">The receptor about to run.</param>
  /// <param name="envelope">The message's envelope.</param>
  /// <param name="context">The scope context the receptor sees.</param>
  /// <param name="scopedProvider">The invocation's scoped provider, or null when the caller has none.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the record is stored.</returns>
  ValueTask RecordAsync(Type receptorType, IMessageEnvelope envelope, IScopeContext? context, IServiceProvider? scopedProvider,
      CancellationToken cancellationToken);

  /// <summary><see cref="RecordAsync"/> for a synchronous receptor: starts the record without waiting for it.</summary>
  /// <param name="receptorType">The receptor about to run.</param>
  /// <param name="envelope">The message's envelope.</param>
  /// <param name="context">The scope context the receptor sees.</param>
  /// <param name="scopedProvider">The invocation's scoped provider.</param>
  void Record(Type receptorType, IMessageEnvelope envelope, IScopeContext? context, IServiceProvider scopedProvider);
}
