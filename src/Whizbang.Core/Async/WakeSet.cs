// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;

#pragma warning disable S2325, S2094, S2326 // a stub that holds the contract's shape until the implementation lands

namespace Whizbang.Core.Async;

/// <summary>
/// The wake sources of a long-running loop, waited on together pass after pass with nothing left behind. Stub: the
/// contract is in <c>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs</c>.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Async/WakeSetTests.cs</tests>
internal sealed class WakeSet : IDisposable {
  /// <summary>Creates the set for a loop.</summary>
  public WakeSet(TimeProvider time, CancellationToken stoppingToken) {
    _ = time;
    _ = stoppingToken;
  }

  /// <summary>Adds a channel.</summary>
  public ChannelWakeSource<T> Add<T>(ChannelReader<T> reader) => throw new NotImplementedException();

  /// <summary>Adds a wake signal.</summary>
  public WakeSource Add(WakeSignal signal) => throw new NotImplementedException();

  /// <summary>Adds a task that completes once.</summary>
  public WakeSource Add(Task once) => throw new NotImplementedException();

  /// <summary>Waits until a source fires, the timeout passes or the loop stops.</summary>
  public ValueTask<Wake> WaitAsync(TimeSpan timeout) => throw new NotImplementedException();

  /// <summary>Releases every wait the set made.</summary>
  public void Dispose() { }
}

/// <summary>Why a wait ended.</summary>
internal enum WakeReason : byte {
  /// <summary>A source fired.</summary>
  Source = 0,
  /// <summary>The timeout passed.</summary>
  Timeout = 1,
  /// <summary>The loop stopped.</summary>
  Stopped = 2,
}

/// <summary>The end of a wait.</summary>
internal readonly record struct Wake(WakeReason Reason, ulong Ready) {
  /// <summary>Stopped.</summary>
  public bool IsStopped => throw new NotImplementedException();
  /// <summary>Timed out.</summary>
  public bool IsTimeout => throw new NotImplementedException();
  /// <summary>Whether a source was ready.</summary>
  public bool Woke(WakeSource source) => throw new NotImplementedException();
}

/// <summary>A wake source.</summary>
internal abstract class WakeSource;

/// <summary>A channel source.</summary>
internal sealed class ChannelWakeSource<T> : WakeSource {
  /// <summary>Whether the channel completed.</summary>
  public bool IsChannelCompleted => throw new NotImplementedException();
}
