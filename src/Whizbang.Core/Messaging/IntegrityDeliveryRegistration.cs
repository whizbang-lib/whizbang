// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The one way an integrity receptor joins the dispatch pipeline (#2001): wrapped, at every stage a framework receptor
/// needs, with its drain handler registered beside it.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs:Registration_WrapsTheReceptorAtAllThreeStagesAsync</tests>
public static class IntegrityDeliveryRegistration {
  /// <summary>
  /// Registers <paramref name="receptor"/> as an integrity receptor: an <see cref="IntegrityDeferringReceptor{TMessage}"/>
  /// at <see cref="LifecycleStage.LocalImmediateInline"/> (local dispatch), <see cref="LifecycleStage.PreOutboxInline"/>
  /// (the sending service's outbox copy) and <see cref="LifecycleStage.PostInboxInline"/> (a copy from another service),
  /// and its handler in the host's <see cref="IntegrityDeliveryRegistry"/>, if one is registered.
  /// </summary>
  /// <typeparam name="TMessage">The integrity message.</typeparam>
  /// <param name="registry">The receptor registry.</param>
  /// <param name="receptor">The receptor that does the work.</param>
  /// <param name="services">The host's services, where the delivery registry is found.</param>
  /// <param name="scopeFactory">Scopes the deferral records a delivery in.</param>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs:Registration_WrapsTheReceptorAtAllThreeStagesAsync</tests>
  public static void RegisterIntegrityReceptor<TMessage>(
      this IReceptorRegistry registry, IReceptor<TMessage> receptor, IServiceProvider services,
      IServiceScopeFactory scopeFactory) where TMessage : IMessage {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(receptor);
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(scopeFactory);
    var deliveries = services.GetService<IntegrityDeliveryRegistry>();
    var deferring = new IntegrityDeferringReceptor<TMessage>(receptor, scopeFactory, deliveries);
    deliveries?.Register(deferring.DeliveryType, deferring.HandleDeferredAsync);
    registry.Register<TMessage>(deferring, LifecycleStage.LocalImmediateInline);
    registry.Register<TMessage>(deferring, LifecycleStage.PreOutboxInline);
    registry.Register<TMessage>(deferring, LifecycleStage.PostInboxInline);
  }
}
