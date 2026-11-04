// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>
/// A value set by the first registration and kept against every later one, atomically, so registrations racing from
/// several threads settle on one value and a value cannot change between two reads (#2021).
/// </summary>
/// <docs>fundamentals/sagas/whizbang-sagas#per-item-streams-cross-pod-safe-fan-out</docs>
/// <tests>tests/Whizbang.Sagas.Component.Tests/FirstRegistrationSlotConcurrencyTests.cs:ConcurrentRegistrations_SettleOnOneValue_ThatEveryRegistrationIsToldAsync</tests>
/// <tests>tests/Whizbang.Sagas.Tests/SagaItemStreamsFirstRegistrationWinsTests.cs</tests>
internal sealed class FirstRegistrationSlot {
  /// <summary>Replaced whole, never mutated, so one reference read is one consistent value.</summary>
  private sealed record Registration(Guid Value);

  private Registration? _registered;

  /// <summary>The registered value, or null before the first registration.</summary>
  public Guid? Current => Volatile.Read(ref _registered)?.Value;

  /// <summary>Registers <paramref name="value"/> unless a value is registered already.</summary>
  /// <param name="value">The value a registration asks for.</param>
  /// <returns>The value in effect afterward: <paramref name="value"/> for the first registration, the first one's after.</returns>
  public Guid Register(Guid value) {
    var prior = Interlocked.CompareExchange(ref _registered, new Registration(value), null);
    return prior?.Value ?? value;
  }

  /// <summary>Tests only: replaces whatever is registered.</summary>
  public void Overwrite(Guid value) => Volatile.Write(ref _registered, new Registration(value));

  /// <summary>Clears the registration; disposing the result puts back exactly what was there, including none.</summary>
  public IDisposable Reset() => new Restore(this, Interlocked.Exchange(ref _registered, null));

  private sealed class Restore(FirstRegistrationSlot slot, Registration? saved) : IDisposable {
    public void Dispose() => Volatile.Write(ref slot._registered, saved);
  }
}
