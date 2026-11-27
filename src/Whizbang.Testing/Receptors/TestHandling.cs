// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Testing.Receptors;

/// <summary>
/// Enters the handling a receptor runs in, for a unit test that calls the receptor's <c>HandleAsync</c> directly (#2609).
/// Inside the returned scope <see cref="Handling.Current"/> is the handling the dispatcher would enter for the same
/// message and receptor, so a strong id's <c>DeriveFrom(Handling.Current, purpose)</c> derives the same id a real
/// handling derives, instead of throwing because the test is outside any handling.
/// </summary>
/// <remarks>
/// <para>
/// The receptor and the service the handling names come from <c>services</c>, where the receptor invoker takes them:
/// the receptor id from the <see cref="IReceptorRegistry"/> entry that runs the receptor (a compiled receptor by its
/// class, a runtime registration by its instance), and the service name from the <see cref="IServiceInstanceProvider"/>,
/// none when there is none. Nothing is computed from a type name, so the ids cannot drift from the ones the dispatcher
/// derives.
/// </para>
/// <para>
/// A receptor the registry does not list for the message is refused: the dispatcher would never run it for that
/// message, so no handling of it exists to derive from. A receptor listed under two ids (a runtime registration at two
/// stages) needs the stage, since each stage's handling derives its own ids.
/// </para>
/// </remarks>
/// <docs>operations/testing/testing-receptors#derived-ids</docs>
/// <tests>tests/Whizbang.Testing.Tests/Receptors/TestHandlingTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/TestHandlingDispatchTests.cs</tests>
public static class TestHandling {
  /// <summary>
  /// Enters a handling of <paramref name="message"/> by <paramref name="receptor"/>, as a fresh local dispatch of it:
  /// a new message id and no handler. Pass the returned scope's <see cref="TestHandlingScope.Envelope"/> to
  /// <see cref="Enter(IServiceProvider, object, IMessageEnvelope, LifecycleStage?)"/> to retry the same handling.
  /// </summary>
  /// <param name="services">The provider the service's receptors are registered in (<c>AddWhizbangReceptorRegistry</c>).</param>
  /// <param name="receptor">The receptor instance the test calls <c>HandleAsync</c> on.</param>
  /// <param name="message">The message the test hands the receptor.</param>
  /// <param name="stage">The stage whose handling to enter; needed only when the receptor is registered under two ids.</param>
  /// <returns>The scope of the handling; disposing it ends the handling and restores the one before it.</returns>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  /// <exception cref="InvalidOperationException">
  /// <paramref name="services"/> has no receptor registry, the registry does not list the receptor for the message, or
  /// lists it under several ids and no stage was given.
  /// </exception>
  /// <tests>tests/Whizbang.Testing.Tests/Receptors/TestHandlingTests.cs:Enter_AMessage_EntersAFreshLocalEnvelopeOfItAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/TestHandlingDispatchTests.cs:TestHandling_AMessage_ThenARealHandlingOfItsEnvelope_DeriveTheSameIdAsync</tests>
  public static TestHandlingScope Enter(IServiceProvider services, object receptor, object message, LifecycleStage? stage = null) {
    ArgumentNullException.ThrowIfNull(message);
    return Enter(services, receptor, new MessageEnvelope<object> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    }, stage);
  }

  /// <summary>
  /// Enters the handling of <paramref name="envelope"/> by <paramref name="receptor"/>: the handling a real dispatch of
  /// that envelope enters, deriving from its message id and its handler.
  /// </summary>
  /// <param name="services">The provider the service's receptors are registered in (<c>AddWhizbangReceptorRegistry</c>).</param>
  /// <param name="receptor">The receptor instance the test calls <c>HandleAsync</c> on.</param>
  /// <param name="envelope">The envelope of the handled message.</param>
  /// <param name="stage">The stage whose handling to enter; needed only when the receptor is registered under two ids.</param>
  /// <returns>The scope of the handling; disposing it ends the handling and restores the one before it.</returns>
  /// <exception cref="ArgumentNullException">An argument is null.</exception>
  /// <exception cref="InvalidOperationException">
  /// <paramref name="services"/> has no receptor registry, the registry does not list the receptor for the message, or
  /// lists it under several ids and no stage was given.
  /// </exception>
  /// <tests>tests/Whizbang.Testing.Tests/Receptors/TestHandlingTests.cs:Enter_AnEnvelope_EntersTheHandlingOfTheRegisteredReceptorInItsServiceAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/TestHandlingDispatchTests.cs:TestHandling_DerivesTheIdARealHandlingOfTheSameEnvelopeDerivesAsync</tests>
  public static TestHandlingScope Enter(IServiceProvider services, object receptor, IMessageEnvelope envelope, LifecycleStage? stage = null) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(receptor);
    ArgumentNullException.ThrowIfNull(envelope);
    var registry = services.GetService<IReceptorRegistry>() ?? throw new InvalidOperationException(
      "The service provider has no receptor registry, so the receptor's id, which its handling derives from, is unknown. " +
      "Register the service's receptors with AddWhizbangReceptorRegistry(), as the host does.");
    var receptorId = _receptorIdOf(registry, receptor, envelope.Payload.GetType(), stage);
    // The service the invoker names: the instance provider's, or none without one
    var serviceName = services.GetService<IServiceInstanceProvider>()?.ServiceName;
    return new TestHandlingScope(ReceptorHandling.Enter(envelope, serviceName, receptorId), envelope);
  }

  // The id the registry runs the receptor under: a runtime registration is its instance, a compiled one its class
  private static string _receptorIdOf(IReceptorRegistry registry, object receptor, Type messageType, LifecycleStage? stage) {
    var receptorType = receptor.GetType();
    LifecycleStage[] stages = stage is { } only ? [only] : Enum.GetValues<LifecycleStage>();
    var ids = stages
      .SelectMany(s => registry.GetReceptorsFor(messageType, s))
      .Where(info => info.RuntimeInstance is null ? info.ReceptorType == receptorType : ReferenceEquals(info.RuntimeInstance, receptor))
      .Select(info => info.ReceptorId)
      .Distinct(StringComparer.Ordinal)
      .ToList();
    var where = stage is { } named ? $" at {named}" : string.Empty;
    return ids.Count switch {
      1 => ids[0],
      0 => throw new InvalidOperationException(
        $"{TypeNameFormatter.ForLog(receptorType)} is not registered for {TypeNameFormatter.ForLog(messageType)}{where} in the service provider's receptor registry, " +
        "so the dispatcher never runs it for this message and there is no handling of it to derive ids from. Register " +
        "the receptor (a compiled one through AddWhizbangReceptorRegistry(), a runtime one through IReceptorRegistry.Register) " +
        "and pass the instance it was registered with."),
      _ => throw new InvalidOperationException(
        $"{TypeNameFormatter.ForLog(receptorType)} is registered for {TypeNameFormatter.ForLog(messageType)} under several receptor ids ({string.Join(", ", ids)}), " +
        "and each stage's handling derives its own ids: pass the stage whose handling the test runs."),
    };
  }
}
