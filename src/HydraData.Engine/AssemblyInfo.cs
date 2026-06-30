// Copyright (c) 2026 crossVault GmbH.

using System.Runtime.CompilerServices;

// Internal DB seams (IConnectionGateway/IDbSlot/IDbExecutor and their implementations) are exercised
// by the engine's own unit and integration test assemblies.
[assembly: InternalsVisibleTo("HydraData.Engine.Tests")]
[assembly: InternalsVisibleTo("HydraData.Engine.IntegrationTests")]
