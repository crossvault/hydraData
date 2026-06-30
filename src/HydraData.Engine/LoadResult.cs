// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// The outcome of a <see cref="StepLoader"/> discovery run.
/// </summary>
/// <param name="Steps">
/// The discovered steps, sorted segment-wise numerically by (GG, SS, TT).
/// </param>
/// <param name="Warnings">
/// Any filename-convention violations found during discovery.
/// An empty list means the directory is clean.
/// </param>
public sealed record LoadResult(
    IReadOnlyList<StepDescriptor> Steps,
    IReadOnlyList<LoaderWarning> Warnings);
