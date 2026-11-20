using System.Threading.Channels;

namespace Exp;

// Compiled with runtime async. Find-UncoveredNewLines.Tests.ps1 names these shapes by line; keep the
// lines where they are or update the tests with them. Every `skip` is false in every run, so each
// function holding one has a block no test ran, and only a line's own evidence can prove it.
public static class AsyncShapes {
  // An await foreach whose loop both continues and ends, in both processes.
  public static async Task<int> DrainAsync(ChannelReader<int> reader, bool skip) {
    if (skip) {
      return -1;
    }
    var sum = 0;
    await foreach (var item in reader.ReadAllAsync()) {
      sum += item;
    }
    return sum;
  }

  // A while (await ...) that ends an await using's scope, both ways in both processes.
  public static async Task<int> CountAsync(Rows rows, bool skip) {
    if (skip) {
      return -1;
    }
    var n = 0;
    await using (var cursor = rows.Open()) {
      while (await cursor.NextAsync()) {
        n++;
      }
    }
    return n;
  }

  // The same loop, never left through its condition: every run throws out of it.
  public static async Task<int> UntilThrowAsync(Rows rows) {
    var n = 0;
    await using (var cursor = rows.Open()) {
      while (await cursor.NextAsync()) {
        n += Rows.Fail();
      }
    }
    return n;
  }

  // A try that ends in a return inside an await using, and a catch that awaits: A completes, B fails.
  public static async Task<int> GuardedAsync(Rows rows, Func<Task> onError) {
    try {
      await using var cursor = rows.Open();
      return await cursor.TakeAsync();
    } catch {
      await onError();
      throw;
    }
  }

  // An empty catch no run enters, then a catch that awaits.
  public static async Task<int> UnenteredAsync(Func<Task> work, Func<Task> onError) {
    try {
      await work();
    } catch (OperationCanceledException) {
      // never entered
    } catch (Exception) {
      await onError();
    }
    return 0;
  }
}

public sealed class Rows(int count) {
  public Cursor Open() => new(count);

  public static int Fail() => throw new InvalidOperationException("boom");

  public sealed class Cursor(int count) : IAsyncDisposable {
    private int _left = count;

    public async Task<bool> NextAsync() {
      await Task.Yield();
      return _left-- > 0;
    }

    public async Task<int> TakeAsync() {
      await Task.Yield();
      ArgumentOutOfRangeException.ThrowIfNegative(_left);
      return _left;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
