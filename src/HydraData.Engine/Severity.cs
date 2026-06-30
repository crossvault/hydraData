// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Outcome severity of a step, ordered so that the numerically highest value is the most severe.
/// </summary>
public enum Severity
{
    /// <summary>The step succeeded.</summary>
    Success = 0,

    /// <summary>The step completed with a non-fatal warning; the transaction still commits.</summary>
    Warning = 1,

    /// <summary>The step failed; the transaction is rolled back.</summary>
    Error = 2,
}
