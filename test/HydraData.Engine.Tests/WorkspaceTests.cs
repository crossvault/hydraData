// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine.Tests.Fakes;
using Xunit;

namespace HydraData.Engine.Tests;

/// <summary>
/// T05.1/T05.2: Workspace RunDir/RunId derivation, lazy subdirectory creation, the
/// <see cref="Workspace.IsInside"/> prefix guard, and the read/write allowlist sandbox.
/// </summary>
public sealed class WorkspaceTests : IDisposable
{
    private static readonly Guid FixedRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _baseDir;

    public WorkspaceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "HydraData-ws-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
                Directory.Delete(_baseDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp files.
        }
    }

    private Workspace NewWorkspace(PumpFolderPolicy? policy = null)
    {
        var runId = new FakeGuidProvider(FixedRunId).NewGuid();
        return new Workspace(_baseDir, runId, policy);
    }

    [Fact]
    public void RunDir_is_predictable_from_base_and_runid()
    {
        var ws = NewWorkspace();

        var expected = Path.GetFullPath(Path.Combine(_baseDir, FixedRunId.ToString("D")));
        Assert.Equal(expected, ws.RunDir);
        Assert.Equal(FixedRunId, ws.RunId);
    }

    [Fact]
    public void Subdirectories_are_not_created_until_accessed()
    {
        var ws = NewWorkspace();

        // No empty skeleton: nothing exists until an accessor is touched.
        Assert.False(Directory.Exists(Path.Combine(ws.RunDir, "out")));
        Assert.False(Directory.Exists(Path.Combine(ws.RunDir, "duck")));
        Assert.False(Directory.Exists(Path.Combine(ws.RunDir, "tmp")));
    }

    [Fact]
    public void Accessing_subdirectories_creates_them_lazily_under_rundir()
    {
        var ws = NewWorkspace();

        Assert.Equal(Path.Combine(ws.RunDir, "out"), ws.Out);
        Assert.Equal(Path.Combine(ws.RunDir, "duck"), ws.Duck);
        Assert.Equal(Path.Combine(ws.RunDir, "tmp"), ws.Tmp);

        Assert.True(Directory.Exists(ws.Out));
        Assert.True(Directory.Exists(ws.Duck));
        Assert.True(Directory.Exists(ws.Tmp));
        Assert.True(Workspace.IsInside(ws.RunDir, ws.Out));
        Assert.True(Workspace.IsInside(ws.RunDir, ws.Duck));
        Assert.True(Workspace.IsInside(ws.RunDir, ws.Tmp));
    }

    [Fact]
    public void IsInside_rejects_sibling_with_shared_textual_prefix()
    {
        var root = Path.Combine(_baseDir, "a", "b");
        var sibling = Path.Combine(_baseDir, "a", "bc");

        Assert.True(Workspace.IsInside(root, root));
        Assert.True(Workspace.IsInside(root, Path.Combine(root, "child.txt")));
        Assert.False(Workspace.IsInside(root, sibling));
        Assert.False(Workspace.IsInside(root, Path.Combine(sibling, "child.txt")));
    }

    [Fact]
    public void RunDir_is_always_readable_and_writable()
    {
        var ws = NewWorkspace(PumpFolderPolicy.Empty);

        var read = ws.ResolveRead("input.csv");
        var write = ws.ResolveWrite(Path.Combine("out", "report.csv"));

        Assert.True(Workspace.IsInside(ws.RunDir, read));
        Assert.True(Workspace.IsInside(ws.RunDir, write));
    }

    [Fact]
    public void Read_outside_allowlist_throws()
    {
        var allowed = Path.Combine(_baseDir, "input");
        Directory.CreateDirectory(allowed);
        var ws = NewWorkspace(new PumpFolderPolicy([allowed], []));

        var outside = Path.Combine(_baseDir, "elsewhere", "secret.csv");

        Assert.Throws<UnauthorizedAccessException>(() => ws.ResolveRead(outside));
        // A file inside the allowed folder resolves.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(allowed, "ok.csv")),
            ws.ResolveRead(Path.Combine(allowed, "ok.csv")));
    }

    [Fact]
    public void Write_outside_allowlist_throws()
    {
        var allowed = Path.Combine(_baseDir, "output");
        Directory.CreateDirectory(allowed);
        var ws = NewWorkspace(new PumpFolderPolicy([], [allowed]));

        var outside = Path.Combine(_baseDir, "elsewhere", "evil.csv");

        Assert.Throws<UnauthorizedAccessException>(() => ws.ResolveWrite(outside));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(allowed, "ok.csv")),
            ws.ResolveWrite(Path.Combine(allowed, "ok.csv")));
    }

    [Fact]
    public void Empty_read_allowlist_with_external_path_throws_with_clear_message()
    {
        var ws = NewWorkspace(PumpFolderPolicy.Empty);
        var external = Path.Combine(_baseDir, "input", "data.csv");

        var ex = Assert.Throws<UnauthorizedAccessException>(() => ws.ResolveRead(external));
        Assert.Contains("allowlist is empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read allowlist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_write_allowlist_with_external_path_throws_with_clear_message()
    {
        var ws = NewWorkspace(PumpFolderPolicy.Empty);
        var external = Path.Combine(_baseDir, "output", "report.csv");

        var ex = Assert.Throws<UnauthorizedAccessException>(() => ws.ResolveWrite(external));
        Assert.Contains("allowlist is empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("write allowlist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Relative_allowlist_entry_is_rejected()
    {
        // PumpFolderPolicy docs require absolute folders; a relative entry would resolve against the
        // process CWD and silently widen the sandbox, so construction must fail fast.
        Assert.Throws<ArgumentException>(
            () => NewWorkspace(new PumpFolderPolicy([Path.Combine("relative", "input")], [])));
        Assert.Throws<ArgumentException>(
            () => NewWorkspace(new PumpFolderPolicy([], [Path.Combine("relative", "output")])));
    }

    [Fact]
    public void Windows_root_relative_and_drive_relative_allowlist_entries_are_rejected()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var rootRelative = Path.DirectorySeparatorChar + Path.Combine("temp", "input");
        var driveLetter = Path.GetPathRoot(_baseDir)![0];
        var driveRelative = $"{driveLetter}:relative{Path.DirectorySeparatorChar}input";

        Assert.True(Path.IsPathRooted(rootRelative));
        Assert.False(Path.IsPathFullyQualified(rootRelative));
        Assert.True(Path.IsPathRooted(driveRelative));
        Assert.False(Path.IsPathFullyQualified(driveRelative));
        Assert.Throws<ArgumentException>(
            () => NewWorkspace(new PumpFolderPolicy([rootRelative], [])));
        Assert.Throws<ArgumentException>(
            () => NewWorkspace(new PumpFolderPolicy([], [driveRelative])));
    }

    [Fact]
    public void Relative_traversal_cannot_escape_the_run_directory()
    {
        // Coverage-only: normalization plus IsInside already reject both shallow and deep traversal.
        var ws = NewWorkspace(PumpFolderPolicy.Empty);

        Assert.Throws<UnauthorizedAccessException>(
            () => ws.ResolveRead(Path.Combine("..", "secret.csv")));
        Assert.Throws<UnauthorizedAccessException>(
            () => ws.ResolveRead(Path.Combine("..", "..", "secret.csv")));
    }

    [Fact]
    public void Windows_allowlist_comparison_accepts_mangled_case_for_existing_directory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var allowed = Path.Combine(_baseDir, "CaseSensitiveInput");
        Directory.CreateDirectory(allowed);
        var mangled = Path.Combine(_baseDir, "cASEsENSITIVEiNPUT");
        Assert.True(Directory.Exists(mangled));

        var ws = NewWorkspace(new PumpFolderPolicy([allowed], []));
        var resolved = ws.ResolveRead(Path.Combine(mangled, "data.csv"));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(allowed, "data.csv")),
            resolved,
            ignoreCase: true);
    }

    [Fact]
    public void Traversal_that_escapes_the_allowlist_is_blocked()
    {
        var allowed = Path.Combine(_baseDir, "input");
        Directory.CreateDirectory(allowed);
        var ws = NewWorkspace(new PumpFolderPolicy([allowed], []));

        // "input/../elsewhere/secret.csv" normalises out of the allowed folder => blocked.
        var escaping = Path.Combine(allowed, "..", "elsewhere", "secret.csv");

        Assert.Throws<UnauthorizedAccessException>(() => ws.ResolveRead(escaping));
    }

    [Fact]
    public void Traversal_that_stays_inside_the_allowlist_is_permitted()
    {
        var allowed = Path.Combine(_baseDir, "input");
        Directory.CreateDirectory(allowed);
        var ws = NewWorkspace(new PumpFolderPolicy([allowed], []));

        // "input/sub/../ok.csv" normalises back into the allowed folder => permitted.
        var staying = Path.Combine(allowed, "sub", "..", "ok.csv");
        var resolved = ws.ResolveRead(staying);

        Assert.Equal(Path.GetFullPath(Path.Combine(allowed, "ok.csv")), resolved);
    }

    [Fact]
    public void Symlink_lexical_path_inside_allowlist_is_reported_inside_no_resolution()
    {
        // Documented limit: IsInside is purely lexical. A path that *names* a
        // location inside the root is treated as inside, even if the OS would follow a symlink elsewhere.
        var root = Path.Combine(_baseDir, "input");
        var linkLikePath = Path.Combine(root, "link", "data.csv");

        Assert.True(Workspace.IsInside(root, linkLikePath));
    }
}
