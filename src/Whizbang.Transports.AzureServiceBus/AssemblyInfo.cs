using System.Runtime.CompilerServices;

// Integration tests exercise the internal SubscribeAsync subscription API that was
// transitioned away from being public. The transport's own comment at
// AzureServiceBusTransport.SubscribeAsync flags this as "internal for backward compat
// during migration"; the migration target is this test assembly. Grant it access.
[assembly: InternalsVisibleTo("Whizbang.Transports.AzureServiceBus.Integration.Tests")]
[assembly: InternalsVisibleTo("Whizbang.Transports.AzureServiceBus.Tests")]
