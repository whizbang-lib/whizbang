using Whizbang.Core.Observability;

namespace Whizbang.Core.AutoPopulate;

/// <summary>
/// Processes auto-populate attributes and stores values in envelope metadata.
/// </summary>
/// <remarks>
/// <para>
/// The processor extracts values from the message hop (which already contains
/// timestamp, security context, service info, and correlation IDs) and stores
/// them in a format that can be used to materialize messages with populated properties.
/// </para>
/// <para>
/// This is called automatically by the Dispatcher at the appropriate lifecycle points.
/// </para>
/// </remarks>
/// <docs>attributes/auto-populate</docs>
public interface IAutoPopulateProcessor {
  /// <summary>
  /// Processes auto-populate registrations for a message and stores values in envelope metadata.
  /// </summary>
  /// <param name="envelope">The message envelope to populate.</param>
  /// <param name="messageType">The runtime type of the message.</param>
  void ProcessAutoPopulate(IMessageEnvelope envelope, Type messageType);
}
