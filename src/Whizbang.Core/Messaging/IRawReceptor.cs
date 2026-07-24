using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Opt-in receptor that receives the envelope's raw <see cref="JsonElement"/> payload instead
/// of a typed CLR message. Useful when the consuming service can't (or doesn't want to)
/// reference the publisher's contracts assembly — e.g., translate-then-republish adapters,
/// generic audit handlers that record any <c>*ChangeRecordedEvent</c>, cross-service
/// back-compat shims during a coordinated contracts roll.
/// </summary>
/// <remarks>
/// <para>
/// Slice 5 of the resilient-transport plan. The dispatcher consults
/// <see cref="IRawReceptorRegistry"/> after the typed binder cascade misses; if a raw receptor
/// is registered for the inbound message type name, the dispatcher invokes it with the
/// envelope's payload as an unparsed <c>JsonElement</c>. The handler extracts whatever fields
/// it cares about manually — there is no compile-time shape check.
/// </para>
/// <para>
/// Precedence is typed &gt; raw: when the binder resolves a CLR type AND a typed
/// <see cref="IReceptor{TMessage}"/> is registered, that path runs and the raw receptor is
/// ignored. Raw receptors are the fallback for "the type didn't resolve, but I still want to
/// handle it."
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/raw-receptors</docs>
public interface IRawReceptor {
  /// <summary>
  /// Full assembly-qualified CLR name of the message type this receptor handles, exactly as
  /// it appears in the inbound envelope's <c>MessageType</c> ApplicationProperty / inbox
  /// <c>message_type</c> column. The dispatcher does string-equal lookup keyed by this value.
  /// </summary>
  string TargetMessageTypeName { get; }

  /// <summary>
  /// Handles the raw payload. The implementation is responsible for any field extraction,
  /// version negotiation, and downstream dispatch.
  /// </summary>
  /// <param name="payload">The envelope's payload as an unparsed JSON element.</param>
  /// <param name="cancellationToken">Cancellation token tied to the dispatch lease.</param>
  Task HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}
