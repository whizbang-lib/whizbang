using Microsoft.Extensions.Logging;

namespace ECommerce.BFF.API.Tests.TestHelpers;

/// <summary>
/// Test implementation of ILogger for testing
/// </summary>
public class TestLogger<T> : ILogger<T> {
  public List<string> LoggedMessages { get; } = new();

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    LoggedMessages.Add(message);
  }
}
