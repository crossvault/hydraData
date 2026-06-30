// Copyright (c) 2026 crossVault GmbH.

using HydraData.Engine;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HydraData.Host.Tests;

/// <summary>
/// Configuration binding and mapping: <c>appsettings.json</c> binds into
/// <see cref="PumpSettings"/>, then <see cref="PumpOptionsMapper"/> produces <see cref="PumpOptions"/> with
/// absolute allowlists and a <see cref="TimeSpan"/> step timeout.
/// </summary>
public class PumpOptionsMapperTests
{
    private static readonly string BaseDir = OperatingSystem.IsWindows() ? @"C:\work\app" : "/work/app";

    private static PumpSettings BindFromJson(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var settings = new PumpSettings();
        config.GetSection(PumpSettings.SectionName).Bind(settings);
        return settings;
    }

    [Fact]
    public void Binds_pump_section_from_appsettings_shape()
    {
        const string json = """
            {
              "Pump": {
                "WorkspaceBase": "./_runs",
                "AllowUnsafeDirectAccess": true,
                "ReadAllowlist": [ "./input", "./more" ],
                "WriteAllowlist": [ "./output" ],
                "StepTimeoutSeconds": 90,
                "RunDirRetentionDays": 7,
                "LegacyGlobalState": true,
                "LegacyGroupBySlug": true,
                "ScriptFolders": [ "./scripts" ],
                "ConnectionsFile": "./connections.xml"
              }
            }
            """;

        var settings = BindFromJson(json);

        Assert.Equal("./_runs", settings.WorkspaceBase);
        Assert.True(settings.AllowUnsafeDirectAccess);
        Assert.Equal(["./input", "./more"], settings.ReadAllowlist);
        Assert.Equal(["./output"], settings.WriteAllowlist);
        Assert.Equal(90, settings.StepTimeoutSeconds);
        Assert.Equal(7, settings.RunDirRetentionDays);
        Assert.True(settings.LegacyGlobalState);
        Assert.True(settings.LegacyGroupBySlug);
        Assert.Equal(["./scripts"], settings.ScriptFolders);
        Assert.Equal("./connections.xml", settings.ConnectionsFile);
    }

    [Fact]
    public void Maps_allowlists_to_absolute_normalised_paths()
    {
        var settings = new PumpSettings
        {
            ReadAllowlist = ["./input", "../shared/in"],
            WriteAllowlist = ["output"],
        };

        var options = PumpOptionsMapper.ToPumpOptions(settings, BaseDir);

        Assert.All(options.Folders.ReadAllowlist, p => Assert.True(Path.IsPathFullyQualified(p)));
        Assert.All(options.Folders.WriteAllowlist, p => Assert.True(Path.IsPathFullyQualified(p)));
        Assert.Equal(Path.GetFullPath("./input", BaseDir), options.Folders.ReadAllowlist[0]);
        Assert.Equal(Path.GetFullPath("../shared/in", BaseDir), options.Folders.ReadAllowlist[1]);
        Assert.Equal(Path.GetFullPath("output", BaseDir), options.Folders.WriteAllowlist[0]);
        // No '..' segment survives normalisation.
        Assert.DoesNotContain("..", options.Folders.ReadAllowlist[1].Split(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Maps_step_timeout_seconds_to_timespan()
    {
        var settings = new PumpSettings { StepTimeoutSeconds = 120 };

        var options = PumpOptionsMapper.ToPumpOptions(settings, BaseDir);

        Assert.Equal(TimeSpan.FromSeconds(120), options.StepTimeout);
    }

    [Fact]
    public void Non_positive_step_timeout_disables_it()
    {
        var options = PumpOptionsMapper.ToPumpOptions(new PumpSettings { StepTimeoutSeconds = 0 }, BaseDir);

        Assert.Null(options.StepTimeout);
    }

    [Fact]
    public void Carries_workspace_and_safemode_and_legacy_switches()
    {
        var settings = new PumpSettings
        {
            WorkspaceBase = "./_runs",
            AllowUnsafeDirectAccess = true,
            LegacyGlobalState = true,
            LegacyGroupBySlug = true,
        };

        var options = PumpOptionsMapper.ToPumpOptions(settings, BaseDir);

        Assert.Equal(Path.GetFullPath("./_runs", BaseDir), options.WorkspaceBase);
        Assert.True(options.AllowUnsafeDirectAccess);
        Assert.True(options.LegacyGlobalState);
        Assert.True(options.LegacyGroupBySlug);
    }

    [Fact]
    public void Resolves_script_folders_and_connections_file_absolute()
    {
        var settings = new PumpSettings
        {
            ScriptFolders = ["./a", "./b"],
            ConnectionsFile = "./conn/connections.xml",
        };

        var folders = PumpOptionsMapper.ResolveScriptFolders(settings, BaseDir);
        var connFile = PumpOptionsMapper.ResolveConnectionsFile(settings, BaseDir);

        Assert.Equal([Path.GetFullPath("./a", BaseDir), Path.GetFullPath("./b", BaseDir)], folders);
        Assert.Equal(Path.GetFullPath("./conn/connections.xml", BaseDir), connFile);
    }
}
