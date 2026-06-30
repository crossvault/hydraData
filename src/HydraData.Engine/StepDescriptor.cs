// Copyright (c) 2026 crossVault GmbH.

namespace HydraData.Engine;

/// <summary>
/// Describes a single step discovered by the <see cref="StepLoader"/>.
/// </summary>
/// <param name="FileName">The bare filename (without directory path).</param>
/// <param name="FilePath">Full path to the script file.</param>
/// <param name="Order">The parsed numeric ordering key.</param>
/// <param name="Meta">Script-level metadata from leading <c>// @tag</c> comments.</param>
public sealed record StepDescriptor(
    string FileName,
    string FilePath,
    StepOrder Order,
    StepMeta Meta);
