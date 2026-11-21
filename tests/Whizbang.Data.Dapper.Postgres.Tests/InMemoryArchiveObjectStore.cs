// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Whizbang.Core.Archival;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// An <see cref="IArchiveObjectStore"/> held in memory, keeping the provider contract exactly: a put never replaces an
/// object, a delete counts an object already gone, and a failure is an <see cref="ArchiveObjectStoreException"/>. Faults
/// are injected per operation, before the request reaches the store or after it has taken effect, so the coordinator's
/// crash safety is shown without any storage service.
/// </summary>
internal sealed class InMemoryArchiveObjectStore : IArchiveObjectStore {
  private readonly ConcurrentDictionary<string, (byte[] Bytes, DateTimeOffset CreatedOn)> _objects = new(StringComparer.Ordinal);
  private readonly TimeProvider _clock;

  public InMemoryArchiveObjectStore(
      ArchiveObjectStoreSettings? settings = null, string container = "archive", string connectionName = "default", TimeProvider? clock = null) {
    Settings = settings ?? new ArchiveObjectStoreSettings();
    Location = new ArchiveLocation(PROVIDER, "memory://account", connectionName, container);
    _clock = clock ?? TimeProvider.System;
  }

  /// <summary>The provider name this store records in every location and locator.</summary>
  public const string PROVIDER = "memory";

  public ArchiveLocation Location { get; }

  public ArchiveObjectStoreSettings Settings { get; }

  /// <summary>Every request in order, as <c>{operation} {path}</c>.</summary>
  public ConcurrentQueue<string> Requests { get; } = new();

  /// <summary>
  /// Fails a request: given the operation and path, returns when it fails (<see cref="Fault.Before"/> leaves the store
  /// unchanged, <see cref="Fault.After"/> lets the request take effect and then reports a failure, as a lost response does).
  /// </summary>
  public Func<string, string?, Fault> Faults { get; set; } = static (_, _) => Fault.None;

  /// <summary>How many requests a fault failed.</summary>
  public int Faulted;

  /// <summary>The stored objects' paths.</summary>
  public IReadOnlyCollection<string> Paths => [.. _objects.Keys.Order(StringComparer.Ordinal)];

  /// <summary>Replaces an object's bytes, as storage that lost or altered an object would hold it.</summary>
  public void Corrupt(string path, byte[] bytes) => _objects[path] = (bytes, _objects[path].CreatedOn);

  /// <summary>Puts an object directly, created at <paramref name="createdOn"/>, bypassing the create-only rule.</summary>
  public void Seed(string path, byte[] bytes, DateTimeOffset? createdOn = null) => _objects[path] = (bytes, createdOn ?? _clock.GetUtcNow());

  public Task PrepareAsync(CancellationToken cancellationToken = default) {
    _request("prepare", null, Fault.None, () => { });
    return Task.CompletedTask;
  }

  public Task<bool> PutAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) {
    var created = false;
    _request("put", path, Faults("put", path), () => created = _objects.TryAdd(path, (content.ToArray(), _clock.GetUtcNow())));
    return Task.FromResult(created);
  }

  public Task<byte[]> GetAsync(string path, CancellationToken cancellationToken = default) {
    byte[]? bytes = null;
    _request("get", path, Faults("get", path), () => {
      if (!_objects.TryGetValue(path, out var stored)) {
        throw new ArchiveObjectStoreException("get", path, Location, new KeyNotFoundException($"No object {path}."));
      }
      bytes = stored.Bytes.ToArray();
    });
    return Task.FromResult(bytes!);
  }

  public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) {
    var existed = false;
    _request("delete", path, Faults("delete", path), () => existed = _objects.TryRemove(path, out _));
    return Task.FromResult(existed);
  }

  public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default) {
    var listed = new List<ArchiveObjectInfo>();
    _request("list", null, Faults("list", null),
      () => listed.AddRange(_objects.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => new ArchiveObjectInfo(o.Key, o.Value.CreatedOn))));
    foreach (var item in listed) {
      await Task.Yield();
      yield return item;
    }
  }

  private void _request(string operation, string? path, Fault fault, Action apply) {
    Requests.Enqueue(path is null ? operation : $"{operation} {path}");
    if (fault == Fault.Before) {
      _fail(operation, path);
    }
    apply();
    if (fault == Fault.After) {
      _fail(operation, path);
    }
  }

  private void _fail(string operation, string? path) {
    Interlocked.Increment(ref Faulted);
    throw new ArchiveObjectStoreException(operation, path, Location, new IOException("injected storage failure"));
  }

  internal enum Fault {
    None,
    Before,
    After,
  }
}
