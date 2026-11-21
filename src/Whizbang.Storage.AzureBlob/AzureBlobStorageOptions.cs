// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Configuration;

namespace Whizbang.Storage.AzureBlob;

/// <summary>
/// Azure Blob Storage for Whizbang (#2502): one storage account, with a container per feature. Bound from
/// <c>Whizbang:Storage:AzureBlob</c> (the account), <c>Whizbang:Storage:AzureBlob:Offloads</c> (message body offload) and
/// <c>Whizbang:Storage:AzureBlob:Archive</c> (the event archive).
/// </summary>
/// <remarks>
/// <para>
/// A feature is active when its options are set (its configuration section exists, or code sets them) and an account is
/// named for it: its own <c>ServiceUri</c> or <c>ConnectionString</c>, or the shared one here. Name the account with
/// exactly one of <see cref="ServiceUri"/> (the identity comes from the <c>Azure.Core.TokenCredential</c> the host
/// registers), <see cref="ConnectionString"/>, or <see cref="ConnectionName"/> naming an entry of the host's
/// <c>ConnectionStrings</c> section.
/// </para>
/// <para>
/// The keys of the earlier packages still bind, each with a startup warning naming its new key: <c>Whizbang:Archive:AzureBlob</c>
/// into <see cref="Archive"/>, every <c>Whizbang:Offloads:AzureBlob:&lt;providerName&gt;</c> into
/// <see cref="NamedOffloads"/>, and <c>Whizbang:BodyOffload:ProviderName</c> as the body store's selector. Where an old
/// and a new key set the same thing, the new key wins.
/// </para>
/// </remarks>
/// <docs>fundamentals/storage/azure-blob#configuration</docs>
/// <tests>tests/Whizbang.Storage.AzureBlob.Tests/AzureBlobStorageConfigurationTests.cs</tests>
public sealed class AzureBlobStorageOptions {
  /// <summary>The section the account is read from.</summary>
  public const string CONFIGURATION_SECTION = "Whizbang:Storage:AzureBlob";

  /// <summary>The section message body offload is read from.</summary>
  public const string OFFLOADS_SECTION = CONFIGURATION_SECTION + ":Offloads";

  /// <summary>The section the event archive is read from.</summary>
  public const string ARCHIVE_SECTION = CONFIGURATION_SECTION + ":Archive";

  /// <summary>The earlier archive package's section, still read; <see cref="ARCHIVE_SECTION"/> wins.</summary>
  public const string LEGACY_ARCHIVE_SECTION = "Whizbang:Archive:AzureBlob";

  /// <summary>The earlier offload package's section, one subsection per provider name, still read.</summary>
  public const string LEGACY_OFFLOADS_SECTION = "Whizbang:Offloads:AzureBlob";

  /// <summary>The body-offload selector's provider name, still read; <c>Whizbang:Storage:AzureBlob:Offloads:ProviderName</c> wins.</summary>
  public const string LEGACY_PROVIDER_NAME_KEY = "Whizbang:BodyOffload:ProviderName";

  /// <summary>
  /// The Blob service endpoint (for example <c>https://account.blob.core.windows.net</c>) every feature uses unless it
  /// names its own account. Authenticated with the <c>Azure.Core.TokenCredential</c> registered in the container, so no
  /// secret is in configuration.
  /// </summary>
  public Uri? ServiceUri { get; set; }

  /// <summary>
  /// A storage connection string every feature uses unless it names its own account: the Azurite emulator
  /// (<c>UseDevelopmentStorage=true</c>) or an account key the host keeps in its secret store. Held as a
  /// <see cref="SensitiveString"/>, so writing the options out writes its redaction.
  /// </summary>
  public SensitiveString? ConnectionString { get; set; }

  /// <summary>
  /// The logical name of the storage connection. When neither <see cref="ServiceUri"/> nor <see cref="ConnectionString"/>
  /// is set, the account is the host's <c>ConnectionStrings:&lt;ConnectionName&gt;</c> (a service endpoint or a connection
  /// string). The archive records it in every location (<see cref="AzureBlobArchiveOptions.ConnectionName"/>), so keep the
  /// name an environment's archive was written with. A name, never a connection string: a value with <c>;</c> or <c>=</c>
  /// is refused.
  /// </summary>
  public string? ConnectionName { get; set; }

  /// <summary>Message body offload through Azure Blob Storage, or <see langword="null"/> to leave it off.</summary>
  public AzureBlobOffloadOptions? Offloads { get; set; }

  /// <summary>Event archive objects in Azure Blob Storage, or <see langword="null"/> to leave the driver's table store.</summary>
  public AzureBlobArchiveOptions? Archive { get; set; }

  /// <summary>
  /// The body stores of the earlier package's keys (<c>Whizbang:Offloads:AzureBlob:&lt;providerName&gt;</c>), by provider
  /// name, each registered under its name as before.
  /// </summary>
  public IDictionary<string, AzureBlobOffloadOptions> NamedOffloads { get; } = new Dictionary<string, AzureBlobOffloadOptions>(StringComparer.Ordinal);

  /// <summary>The key that named the shared account, or <see langword="null"/> when code set it.</summary>
  internal string? AccountSource { get; set; }

  /// <summary>Why the archive uses Azure Blob Storage, or why it does not, as the startup report writes it.</summary>
  internal string ArchiveReason { get; set; } = $"{ARCHIVE_SECTION} is not set";

  /// <summary>Why body offload uses Azure Blob Storage, as the startup report writes it.</summary>
  internal string OffloadReason { get; set; } = $"{OFFLOADS_SECTION} is not set";

  /// <summary>The warnings binding gave (a key of an earlier package, a feature with no account), logged at start.</summary>
  internal List<string> Notices { get; } = [];
}
