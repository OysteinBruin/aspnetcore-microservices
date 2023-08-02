/*
 */

using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Git;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.IO.FileSystemTasks;

class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.Pack);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    [Parameter] 
    string NugetApiUrl = "https://api.nuget.org/v3/index.json"; //default
    [Parameter] 
    string NugetApiKey;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion] readonly GitVersion GitVersion;


    
    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ProjectPath => SourceDirectory / "Aspnetcore.MicroServices.Common" / "Aspnetcore.MicroServices.Common.csproj";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath NugetDirectory => ArtifactsDirectory / "nuget";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            //TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            //AnalyzerDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(NugetDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(_ => _
                .SetProjectFile(ProjectPath));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(ProjectPath)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .EnableNoRestore());
        });
    
    /*
     TODO: Unit tests - Ref quartznet - https://github.com/quartznet/quartznet/blob/d509b46084c41f7f61e65ff781292866f2ea946e/build/Build.cs
    Target UnitTest => _ => _
        .After(Compile)
        .Executes(() =>
        {
            var framework = "";
            if (!IsRunningOnWindows)
            {
                framework = "net6.0";
            }

            var testProjects = new[] { "Quartz.Tests.Unit", "Quartz.Tests.AspNetCore" };
            DotNetTest(s => s
                .EnableNoRestore()
                .EnableNoBuild()
                .SetConfiguration(Configuration)
                .SetFramework(framework)
                .SetLoggers(GitHubActions.Instance is not null ? new[] { "GitHubActions" } : Array.Empty<string>())
                .CombineWith(testProjects, (_, testProject) => _
                    .SetProjectFile(Solution.GetAllProjects(testProject).First())
                )
            );
        });

    Target IntegrationTest => _ => _
        .After(Compile)
        .OnlyWhenDynamic(() => Host is GitHubActions && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        .Executes(() =>
        {
            Log.Information("Starting Postgres");
            ProcessTasks.StartProcess("sudo", "systemctl start postgresql.service").AssertZeroExitCode();
            ProcessTasks.StartProcess("pg_isready").AssertZeroExitCode();

            static void RunAsPostgresUser(string parameters)
            {
                // Warn: Be careful refactoring this to concatenation. 
                ProcessTasks.StartProcess("sudo", "-u postgres " + parameters, workingDirectory: Path.GetTempPath()).AssertZeroExitCode();
            }

            Log.Information("Creating user...");
            RunAsPostgresUser("psql --command=\"CREATE USER quartznet PASSWORD 'quartznet'\" --command=\"\\du\"");

            Log.Information("Creating database...");
            RunAsPostgresUser("createdb --owner=quartznet quartznet");

            static void RunPsqlAsQuartznetUser(string parameters)
            {
                // Warn: Be careful refactoring this to concatenation
                ProcessTasks.StartProcess("psql", $"--username=quartznet --host=localhost " + parameters, environmentVariables: new Dictionary<string, string> { { "PGPASSWORD", "quartznet" } }).AssertZeroExitCode();
            }

            RunPsqlAsQuartznetUser("--list quartznet");

            Log.Information("Creating schema...");
            RunPsqlAsQuartznetUser("-d quartznet -f ./database/tables/tables_postgres.sql");

            var integrationTestProjects = new[] { "Quartz.Tests.Integration" };
            DotNetTest(s => s
                .EnableNoRestore()
                .EnableNoBuild()
                .SetConfiguration(Configuration)
                .SetFramework("net6.0")
                .SetLoggers("GitHubActions")
                .SetFilter("TestCategory!=db-firebird&TestCategory!=db-oracle&TestCategory!=db-mysql&TestCategory!=db-sqlserver")
                .CombineWith(integrationTestProjects, (_, testProject) => _
                    .SetProjectFile(Solution.GetAllProjects(testProject).First())
                )
            );
        });
    */

    Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            string nuGetVersionCustom = GitVersion.NuGetVersionV2;

            //if it's not a tagged release - append the commit number to the package version
            //tagged commits on master have versions
            // - v0.3.0-beta
            //other commits have
            // - v0.3.0-beta1

            if (Int32.TryParse(GitVersion.CommitsSinceVersionSource, out int commitNum))
                nuGetVersionCustom = commitNum > 0 ? nuGetVersionCustom + $"{commitNum}" : nuGetVersionCustom;
            
            
            Console.WriteLine($"Execute Pack - project path {ProjectPath}, artifacts directory {ArtifactsDirectory}");
            DotNetPack(s => s
                .SetProject(ProjectPath)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetVersion(nuGetVersionCustom)
                .SetDescription("Common configuration and implementations for my .Net microservices projects")
                .SetPackageTags("microservice .net asp-net c# library")
                .SetNoDependencies(true)
                .SetOutputDirectory(NugetDirectory));

        });
    
    Target Push => _ => _
        .DependsOn(Pack)
        .Requires(() => NugetApiUrl)
        .Requires(() => NugetApiKey)
        .Requires(() => Configuration.Equals(Configuration.Release))
        .Executes(() =>
        {
            NugetDirectory.GlobFiles("*.nupkg")
                .Where(x => !x.HasExtension("symbols.nupkg"))
                .ForEach(x =>
                {
                    Console.WriteLine($"Execute Push - target path {x}, source {NugetApiUrl}");
                    DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource(NugetApiUrl)
                        .SetApiKey(NugetApiKey)
                    );
                });
        });
}
