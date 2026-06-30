// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Default <see cref="IConnectionGateway"/>. Opens one <see cref="DbSlot"/> per call; reuse per
/// <see cref="ConnectionInfo.Id"/> within a step is the caller's responsibility (e.g. the step runner),
/// by design
/// </summary>
internal sealed class ConnectionGateway : IConnectionGateway
{
    /// <inheritdoc />
    public IDbSlot Open(ConnectionInfo info, int? commandTimeoutSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new DbSlot(info, commandTimeoutSeconds);
    }
}
