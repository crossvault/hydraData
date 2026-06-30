// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// A non-fatal diagnostic produced while parsing <c>connections.xml</c>. Warnings never abort
/// parsing; duplicate ids additionally cause a hard error when that id is resolved
///.
/// </summary>
/// <param name="Message">Human-readable warning text.</param>
public sealed record ConnectionRegistryWarning(string Message);
