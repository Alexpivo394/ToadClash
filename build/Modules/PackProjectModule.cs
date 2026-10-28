using Build.Options;
using EnumerableAsyncProcessor.Extensions;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using Sourcy.DotNet;

namespace Build.Modules;

/// <summary>
///     Pack Geometry, Rules and Engine versioned {release}, and one Alexpivo394.Revit.Clash.Revit package per Revit year,
///     versioned {year}.{release}. The release goes in as ClashVersion, not Version: a global Version would flow into the
///     referenced Geometry project and turn the Revit package's dependency into Geometry {year}.{release}.
/// </summary>
[DependsOn<ResolveVersioningModule>]
[DependsOn<CleanProjectModule>(Optional = true)]
public sealed class PackProjectModule(IOptions<BuildOptions> buildOptions) : Module
{
    private static readonly int[] RevitYears = [2020, 2021, 2022, 2023, 2024, 2025, 2026, 2027];

    protected override async Task ExecuteModuleAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var versioningResult = await context.GetModule<ResolveVersioningModule>();
        var versioning = versioningResult.ValueOrDefault!;
        var outputFolder = context.Git().RootDirectory.GetFolder(buildOptions.Value.OutputDirectory);

        string[] libraries =
        [
            Projects.Alexpivo394_Revit_Clash_Geometry.FullName,
            Projects.Alexpivo394_Revit_Clash_Rules.FullName,
            Projects.Alexpivo394_Revit_Clash_Engine.FullName
        ];

        foreach (var library in libraries)
        {
            await context.DotNet().Pack(new DotNetPackOptions
            {
                ProjectSolution = library,
                Configuration = "Release",
                Output = outputFolder,
                Properties = new List<KeyValue> { ("ClashVersion", versioning.Version) }
            }, cancellationToken: cancellationToken);
        }

        await RevitYears
            .SelectAsync(async year => await context.DotNet().Pack(new DotNetPackOptions
            {
                ProjectSolution = Projects.Alexpivo394_Revit_Clash_Revit.FullName,
                Configuration = $"Release.R{year % 100}",
                Output = outputFolder,
                Properties = new List<KeyValue> { ("ClashVersion", versioning.Version) }
            }, cancellationToken: cancellationToken), cancellationToken)
            .ProcessOneAtATime();
    }
}
