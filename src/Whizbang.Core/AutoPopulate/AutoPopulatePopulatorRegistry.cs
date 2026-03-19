using Whizbang.Core.Observability;
using Whizbang.Core.Registry;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.AutoPopulate;

/// <summary>
/// Static registry aggregating all auto-populate populators from loaded assemblies.
/// </summary>
/// <remarks>
/// <para>
/// This class uses the <see cref="AssemblyRegistry{T}"/> pattern for multi-assembly
/// contributions. Each assembly with auto-populated record types generates an
/// <see cref="IAutoPopulatePopulator"/> implementation that self-registers via
/// [ModuleInitializer] at load time.
/// </para>
/// <para>
/// Unlike <see cref="AutoPopulateRegistry"/> which provides metadata for JSON manipulation,
/// this registry provides typed populators that use record <c>with</c> expressions for
/// zero-reflection, AOT-compatible property population.
/// </para>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
public static class AutoPopulatePopulatorRegistry {
  /// <summary>
  /// Register an assembly's auto-populate populator.
  /// Called from generated [ModuleInitializer] code.
  /// </summary>
  /// <param name="populator">The populator to register.</param>
  /// <param name="priority">Lower = tried first. Contracts should use 100, services use 1000.</param>
  public static void Register(IAutoPopulatePopulator populator, int priority = 1000) {
    AssemblyRegistry<IAutoPopulatePopulator>.Register(populator, priority);
  }

  /// <summary>
  /// Populates SentAt-phase properties on a message.
  /// Iterates registered populators and returns the first non-null result, or the original message.
  /// </summary>
  /// <param name="message">The message to populate.</param>
  /// <param name="hop">The message hop containing timestamp, scope, and service info.</param>
  /// <param name="messageId">The message ID for identifier population.</param>
  /// <returns>A new message instance with populated properties, or the original message if no populator handles it.</returns>
  public static object PopulateSent(object message, MessageHop hop, MessageId messageId) {
    foreach (var populator in AssemblyRegistry<IAutoPopulatePopulator>.GetOrderedContributions()) {
      var result = populator.TryPopulateSent(message, hop, messageId);
      if (result is not null) {
        return result;
      }
    }
    return message;
  }

  /// <summary>
  /// Populates QueuedAt-phase properties on a message.
  /// Iterates registered populators and returns the first non-null result, or the original message.
  /// </summary>
  /// <param name="message">The message to populate.</param>
  /// <param name="timestamp">The timestamp when the message was queued.</param>
  /// <returns>A new message instance with populated properties, or the original message if no populator handles it.</returns>
  public static object PopulateQueued(object message, DateTimeOffset timestamp) {
    foreach (var populator in AssemblyRegistry<IAutoPopulatePopulator>.GetOrderedContributions()) {
      var result = populator.TryPopulateQueued(message, timestamp);
      if (result is not null) {
        return result;
      }
    }
    return message;
  }

  /// <summary>
  /// Populates DeliveredAt-phase properties on a message.
  /// Iterates registered populators and returns the first non-null result, or the original message.
  /// </summary>
  /// <param name="message">The message to populate.</param>
  /// <param name="timestamp">The timestamp when the message was delivered.</param>
  /// <returns>A new message instance with populated properties, or the original message if no populator handles it.</returns>
  public static object PopulateDelivered(object message, DateTimeOffset timestamp) {
    foreach (var populator in AssemblyRegistry<IAutoPopulatePopulator>.GetOrderedContributions()) {
      var result = populator.TryPopulateDelivered(message, timestamp);
      if (result is not null) {
        return result;
      }
    }
    return message;
  }

  /// <summary>
  /// Count of registered auto-populate populators (for diagnostics/testing).
  /// </summary>
  public static int Count => AssemblyRegistry<IAutoPopulatePopulator>.Count;
}
