namespace Exp;

public sealed class Sink {
  public int Total { get; private set; }
  public void Record(int n) => Total += n;
}

public sealed class Bomb {
  public void Fail() => throw new InvalidOperationException("boom");
}

public sealed class Shapes {
  private readonly Sink? _sink;
  private readonly Sink? _never;

  public Shapes(Sink? sink) {
    _sink = sink;
    _never = null;
  }

  public int Split(int n, bool rare) {
    _sink?.Record(n);
    if (rare) {
      return -1;
    }
    return n;
  }

  public async Task<int> SplitAsync(Sink? local, int n, bool rare) {
    await Task.Yield();
    local?.Record(n);
    if (rare) {
      return -1;
    }
    return n;
  }

  public int Unsplit(int n, bool rare) {
    _never?.Record(n);
    if (rare) {
      return -1;
    }
    return n;
  }

  public static int Guarded(Sink? sink, bool rare) {
    if (sink?.Total >= 0) {
      return 1;
    }
    if (rare) {
      return -1;
    }
    return 0;
  }

  public static void Throwing(Bomb? bomb, bool rare) {
    bomb?.Fail();
    if (rare) {
      return;
    }
  }

  public static int Continued(Func<Sink?> get, int n) {
    get()
      ?.Record(n);
    return n;
  }

  public static int Nested(Sink? sink, int n) {
    var totals = new List<int> {
      sink?.Total ?? n,
    };
    return totals[0];
  }

  public static int Chain(List<Sink?> items) {
    var count = items
      .Where(i => i?.Total > 0)
      .Count();
    return count;
  }
}

public interface IMeterLookup {
  T? Find<T>() where T : class;
}

public static class Sweeper {
  public static async Task<long> SweepAsync(IMeterLookup lookup, Sink? metrics, IEnumerable<int> rows, bool rare) {
    await Task.Yield();
    var swept = 0L;
    foreach (var r in rows) {
      swept += r;
      metrics?.Record(r);
    }
    lookup.Find<Sink>()
      ?.Record((int)swept);
    if (rare) {
      return -1;
    }
    return swept;
  }
}
