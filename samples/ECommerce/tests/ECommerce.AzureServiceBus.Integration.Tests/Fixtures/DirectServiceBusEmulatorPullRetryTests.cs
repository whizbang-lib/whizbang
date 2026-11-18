// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Which failed image pulls the Service Bus emulator fixture retries.
/// </summary>
/// <remarks>
/// A merge-queue run lost every test in the suite to one reset connection while pulling the mssql image from
/// mcr.microsoft.com (run 44336589626). A registry network failure is retried; anything else fails at once, so a
/// wrong image name or a stopped daemon is not hidden behind retries.
/// </remarks>
public class DirectServiceBusEmulatorPullRetryTests {
  [Test]
  [Arguments("mssql Error Get \"https://mcr.microsoft.com/v2/mssql/server/manifests/sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f\": read tcp 10.0.135.164:44916->150.171.69.10:443: read: connection reset by peer")]
  [Arguments("Error response from daemon: Get \"https://mcr.microsoft.com/v2/\": net/http: TLS handshake timeout")]
  [Arguments("Error response from daemon: Get \"https://mcr.microsoft.com/v2/\": dial tcp 150.171.69.10:443: i/o timeout")]
  [Arguments("failed to copy: httpReadSeeker: failed open: unexpected EOF")]
  [Arguments("Error response from daemon: received unexpected HTTP status: 503 Service Unavailable")]
  [Arguments("Error response from daemon: toomanyrequests: retry later")]
  public async Task RegistryNetworkFailure_IsRetriedAsync(string error) {
    await Assert.That(DirectServiceBusEmulatorFixture.IsTransientPullFailure(error)).IsTrue();
  }

  [Test]
  [Arguments("Error response from daemon: manifest for mcr.microsoft.com/mssql/server:nope not found: manifest unknown")]
  [Arguments("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")]
  [Arguments("Error response from daemon: unauthorized: authentication required")]
  [Arguments("")]
  public async Task OtherFailure_IsNotRetriedAsync(string error) {
    await Assert.That(DirectServiceBusEmulatorFixture.IsTransientPullFailure(error)).IsFalse();
  }
}
