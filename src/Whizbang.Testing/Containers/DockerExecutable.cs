namespace Whizbang.Testing.Containers;

/// <summary>
/// Resolves the <c>docker</c> CLI to an absolute file path so that
/// <see cref="System.Diagnostics.ProcessStartInfo"/> does not have to consult
/// the <c>PATH</c> environment variable (which an attacker could poison).
/// </summary>
/// <remarks>
/// The resolved path is cached for the lifetime of the process. Only absolute
/// paths to known, expected install locations are accepted — this addresses
/// Sonar rule <c>csharpsquid:S4036</c>.
/// </remarks>
public static class DockerExecutable {
  private static readonly Lock _resolveLock = new();
  private static string? _resolvedPath;

  private static readonly string[] _wellKnownUnixPaths = [
    "/usr/local/bin/docker",
    "/usr/bin/docker",
    "/opt/homebrew/bin/docker",
    "/Applications/Docker.app/Contents/Resources/bin/docker",
  ];

  private static readonly string[] _wellKnownWindowsPaths = [
    @"C:\Program Files\Docker\Docker\resources\bin\docker.exe",
    @"C:\ProgramData\DockerDesktop\version-bin\docker.exe",
  ];

  /// <summary>
  /// Gets the absolute path to the <c>docker</c> executable, or <c>null</c>
  /// if none of the well-known install locations exist on this machine.
  /// </summary>
  public static string? Path {
    get {
      if (_resolvedPath is not null) {
        return _resolvedPath;
      }

      lock (_resolveLock) {
        _resolvedPath ??= _resolve();
        return _resolvedPath;
      }
    }
  }

  /// <summary>
  /// Gets the absolute path to the <c>docker</c> executable, throwing when
  /// it cannot be located.
  /// </summary>
  /// <exception cref="InvalidOperationException">
  /// Thrown when docker is not installed at any well-known absolute path.
  /// </exception>
  public static string PathOrThrow =>
    Path ?? throw new InvalidOperationException(
      "Docker executable could not be located at any well-known absolute path. " +
      "Install Docker Desktop or the Docker CLI, or set the DOCKER_CLI environment " +
      "variable to an absolute path.");

  private static string? _resolve() {
    var envOverride = Environment.GetEnvironmentVariable("DOCKER_CLI");
    if (!string.IsNullOrWhiteSpace(envOverride)
        && System.IO.Path.IsPathFullyQualified(envOverride)
        && File.Exists(envOverride)) {
      return envOverride;
    }

    var candidates = OperatingSystem.IsWindows() ? _wellKnownWindowsPaths : _wellKnownUnixPaths;
    foreach (var candidate in candidates) {
      if (File.Exists(candidate)) {
        return candidate;
      }
    }

    return null;
  }
}
