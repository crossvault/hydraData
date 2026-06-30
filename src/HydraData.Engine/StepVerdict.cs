// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Exception that carries a <see cref="StepResult"/> as a step's verdict. Thrown by the script
/// surface (e.g. <c>Fail</c>/<c>Expect</c>) to short-circuit a step with a defined result.
/// </summary>
public sealed class StepVerdict : Exception
{
    /// <summary>Initializes a new instance carrying the given <paramref name="result"/>.</summary>
    /// <param name="result">The result this verdict reports.</param>
    public StepVerdict(StepResult result)
        : base(result?.Message ?? throw new ArgumentNullException(nameof(result)))
    {
        Result = result;
    }

    /// <summary>The result carried by this verdict.</summary>
    public StepResult Result { get; }
}
