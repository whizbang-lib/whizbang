// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1031, RCS1075 // a chaos forwarder: every socket failure is the partition it simulates
using System.Net;
using System.Net.Sockets;

namespace Whizbang.Data.EFCore.Postgres.Tests.Chaos;

/// <summary>
/// A loopback TCP forwarder between one instance and the database, so a test can partition that
/// instance alone: <see cref="Cut"/> drops every open connection and refuses new ones until
/// <see cref="Heal"/>. Every other instance keeps talking to the database directly.
/// </summary>
internal sealed class ChaosTcpProxy : IAsyncDisposable {
  private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
  private readonly string _targetHost;
  private readonly int _targetPort;
  private readonly CancellationTokenSource _stop = new();
  private readonly Lock _gate = new();
  private readonly List<TcpClient> _open = [];
  private readonly Task _accepting;
  private bool _cut;

  public ChaosTcpProxy(string targetHost, int targetPort) {
    _targetHost = targetHost;
    _targetPort = targetPort;
    _listener.Start();
    _accepting = _acceptAsync();
  }

  /// <summary>The port instances connect to instead of the database's.</summary>
  public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

  /// <summary>Partitions: every open connection drops, and new ones are closed on arrival.</summary>
  public void Cut() {
    lock (_gate) {
      _cut = true;
      foreach (var client in _open) {
        client.Dispose();
      }
      _open.Clear();
    }
  }

  /// <summary>Ends the partition: new connections reach the database again.</summary>
  public void Heal() {
    lock (_gate) {
      _cut = false;
    }
  }

  private async Task _acceptAsync() {
    while (!_stop.IsCancellationRequested) {
      TcpClient inbound;
      try {
        inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
      } catch (OperationCanceledException) {
        return;
      } catch (ObjectDisposedException) {
        return;
      }
      _ = _pipeAsync(inbound);
    }
  }

  private async Task _pipeAsync(TcpClient inbound) {
    var outbound = new TcpClient();
    lock (_gate) {
      if (_cut) {
        inbound.Dispose();
        outbound.Dispose();
        return;
      }
      _open.Add(inbound);
      _open.Add(outbound);
    }
    try {
      await outbound.ConnectAsync(_targetHost, _targetPort, _stop.Token);
      var a = _copyAsync(inbound.GetStream(), outbound.GetStream());
      var b = _copyAsync(outbound.GetStream(), inbound.GetStream());
      await Task.WhenAny(a, b);
    } catch (Exception) {
      // A cut connection ends here; the instance sees its socket close.
    } finally {
      inbound.Dispose();
      outbound.Dispose();
    }
  }

  private async Task _copyAsync(NetworkStream from, NetworkStream to) {
    try {
      await from.CopyToAsync(to, _stop.Token);
    } catch (Exception) {
      // either side closing ends the copy
    }
  }

  public async ValueTask DisposeAsync() {
    await _stop.CancelAsync();
    _listener.Stop();
    Cut();
    try {
      await _accepting;
    } catch (Exception) {
      // stopping
    }
    _stop.Dispose();
  }
}
