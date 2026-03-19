namespace Whizbang.Core.Observability;

/// <summary>
/// Caller information captured at dispatch time.
/// Provides the method name, file path, and line number of the code that dispatched the message.
/// </summary>
/// <docs>fundamentals/messages/message-context#caller-info</docs>
/// <tests>Whizbang.Core.Tests/Observability/CallerInfoTests.cs</tests>
public interface ICallerInfo {
  /// <summary>
  /// The name of the calling method that dispatched the message.
  /// </summary>
  string CallerMemberName { get; }

  /// <summary>
  /// The file path of the calling code that dispatched the message.
  /// </summary>
  string CallerFilePath { get; }

  /// <summary>
  /// The line number of the calling code that dispatched the message.
  /// </summary>
  int CallerLineNumber { get; }
}
