namespace Whizbang.Sagas;

/// <summary>
/// Emitted after the saga's items have been dispatched to their
/// handlers. Distinguishes "items planned" (<see cref="ISagaInitiatedEvent"/>)
/// from "items actually started" (this event), so the saga model can
/// track dispatch failures separately from item failures.
/// </summary>
public interface ISagaItemsDispatchedEvent : ISagaEvent {

  /// <summary>Total items intended for dispatch.</summary>
  int TotalItems { get; }

  /// <summary>Items that were successfully dispatched to a handler.</summary>
  int SuccessfullyDispatched { get; }

  /// <summary>Items that could not be dispatched (transport refused, validation failed pre-dispatch, etc.).</summary>
  int FailedToDispatch { get; }
}
