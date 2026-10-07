// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// A REST lens request asked for something the endpoint cannot do: a filter or sort on a field it
/// does not offer, or a filter value that cannot be read as its field's type. Generated endpoints
/// answer it with 400 and this message.
/// </summary>
/// <docs>apis/rest/filtering#invalid-requests</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/LensQueryShapingTests.cs</tests>
public sealed class InvalidLensRequestException : Exception {
  /// <summary>Creates the exception with no message.</summary>
  public InvalidLensRequestException() { }

  /// <summary>Creates the exception with a message.</summary>
  /// <param name="message">What was wrong with the request.</param>
  public InvalidLensRequestException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and its cause.</summary>
  /// <param name="message">What was wrong with the request.</param>
  /// <param name="innerException">The cause.</param>
  public InvalidLensRequestException(string message, Exception innerException) : base(message, innerException) { }

  /// <summary>A filter or sort named a field the endpoint does not offer.</summary>
  /// <param name="parameter">The parameter that named it: <c>filter</c> or <c>sort</c>.</param>
  /// <param name="field">The field it named.</param>
  /// <returns>The exception to throw.</returns>
  public static InvalidLensRequestException UnknownField(string parameter, string field) =>
    new($"'{field}' is not a field this endpoint can {parameter} by.");

  /// <summary>A filter value cannot be read as its field's type.</summary>
  /// <param name="field">The field filtered.</param>
  /// <param name="value">The value given.</param>
  /// <returns>The exception to throw.</returns>
  public static InvalidLensRequestException InvalidValue(string field, string? value) =>
    new($"'{value}' is not a valid value for filter[{field}].");
}
