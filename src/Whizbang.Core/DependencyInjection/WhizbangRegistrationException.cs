using System.Text;

namespace Whizbang.Core.DependencyInjection;

/// <summary>
/// Thrown when a composed service collection cannot satisfy a registered type's constructor.
/// </summary>
/// <remarks>
/// <para>
/// The failure this replaces is silence. Without validation the container supplies null, the
/// dependent type runs in a degraded mode nobody chose, and the missing behavior looks exactly like
/// behavior that was never requested. That has cost real production incidents where a worker never
/// started and an audit trail could not name its writer.
/// </para>
/// <para>
/// The message names every gap at once, and for each one both the missing service and the type that
/// wanted it. Reporting only the first turns a five-gap composition into five fix-and-rerun cycles;
/// reporting only the service leaves an engineer grepping for who wanted it.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/RegistrationValidationTests.cs</tests>
public sealed class WhizbangRegistrationException : Exception {

  /// <summary>Every dependency that no registration provides.</summary>
  public IReadOnlyList<MissingRegistration> Missing { get; }

  /// <summary>Creates an exception describing the missing registrations.</summary>
  /// <param name="missing">The gaps found during validation.</param>
  public WhizbangRegistrationException(IReadOnlyList<MissingRegistration> missing)
      : base(_buildMessage(missing)) {
    Missing = missing;
  }

  /// <summary>Creates an exception with a message.</summary>
  /// <param name="message">The message.</param>
  public WhizbangRegistrationException(string message) : base(message) {
    Missing = [];
  }

  /// <summary>Creates an exception with a message and inner exception.</summary>
  /// <param name="message">The message.</param>
  /// <param name="innerException">The inner exception.</param>
  public WhizbangRegistrationException(string message, Exception innerException)
      : base(message, innerException) {
    Missing = [];
  }

  /// <summary>Creates an exception with no detail.</summary>
  public WhizbangRegistrationException() {
    Missing = [];
  }

  private static string _buildMessage(IReadOnlyList<MissingRegistration> missing) {
    ArgumentNullException.ThrowIfNull(missing);

    var sb = new StringBuilder();
    sb.Append("Whizbang registration validation failed: ")
      .Append(missing.Count)
      .Append(missing.Count == 1 ? " dependency is" : " dependencies are")
      .AppendLine(" declared but not registered.");

    for (var i = 0; i < missing.Count; i++) {
      sb.Append("  - ")
        .Append(missing[i].MissingService.Name)
        .Append(" (required by ")
        .Append(missing[i].NeededBy.Name)
        .AppendLine(")");
    }

    sb.AppendLine("Register each service, or call the Add* extension that supplies its default.");
    return sb.ToString();
  }
}
