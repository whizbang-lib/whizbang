namespace Whizbang.Core;

/// <summary>
/// Receptors receive messages (commands) and produce responses (events).
/// They are stateless decision-making components that apply business rules
/// and emit events representing decisions made.
/// </summary>
/// <typeparam name="TMessage">The type of message this receptor handles</typeparam>
/// <typeparam name="TResponse">The type of response this receptor produces</typeparam>
public interface IReceptor<in TMessage, TResponse> {
    /// <summary>
    /// Receives a message, applies business logic, and returns a response.
    /// </summary>
    /// <param name="message">The message to process</param>
    /// <returns>The response representing the decision made</returns>
    Task<TResponse> ReceiveAsync(TMessage message);
}
