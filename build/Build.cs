using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.PowerShell.PowerShellTasks;
using static Nuke.Common.IO.CompressionTasks;
using Nuke.Common.Git;
using Nuke.Common.Tools.MSBuild;
using Microsoft.Build.Tasks;
using Azure.ResourceManager;
using Azure.Identity;
using Azure.ResourceManager.Resources;
using Azure.Core;
using Nuke.Common.Tools.PowerShell;
using System.Net.Sockets;

[GitHubActions(
    "continuous",
    GitHubActionsImage.UbuntuLatest,
    On = new[] { GitHubActionsTrigger.Push },    
    InvokedTargets = new[] { nameof(DeployCode) },
    ImportSecrets = new[] { nameof(SpSecret) }) ]
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.DeployCode);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution("./NukeApp/NukeApp.sln")]
    readonly Solution Solution;

    [Parameter("SpSecret")]
    [Secret]
    readonly string SpSecret;

    [Parameter]    
    readonly string TenantId;

    [Parameter]
    readonly string SubscriptionId;
    
    [Parameter]
    readonly string ResourceGroup;

    [Parameter]
    readonly string AppId;

    [GitRepository] readonly GitRepository Repository;

    Target Clean => _ => _
        .DependentFor(Restore)
        .Executes(() =>
        {            
            DotNet($"clean {Solution.Path}");
        });

    Target Restore => _ => _
        .Executes(() =>
        {            
            DotNet($"restore {Solution.Path}");
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNet($"build {Solution.Path}");
        });

    Target UnitTest => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNet($"test {Solution.Path}");
        });

    Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            var project = Solution.Projects.First(x => x.Name == "NukeApp");

            var output = GetOutputPath(project);            
            var path = output / "NukeApp.zip";

            var wwwrooot = project.Directory;
            
            DeleteFile(path);
            CompressZip(output, output / "NukeApp.zip", compressionLevel: CompressionLevel.SmallestSize, fileMode: FileMode.CreateNew);
        });

    Target ServerAuthentication => _ => _        
        .Executes(() =>
        {
            if (IsServerBuild)
            {
                var appId = AppId;
                var secret = SpSecret;
                var tenantId = TenantId;

                PowerShell( _ => _.SetProcessToolPath("pwsh").SetCommand($"az login --service-principal -u {appId} -p {secret} --tenant {tenantId}"));
            } else
            {
                Log.Information("Running Locally");
            }
        });

    Target DeployInfrastructure => _ => _
    .DependsOn(ServerAuthentication)
    .Executes(() =>
    {
        var resourceGroupName = ResourceGroup;
        var subscriptionId = SubscriptionId;

        var armTemplatePath = RootDirectory / "ArmTemplates" / "template.json";

        PowerShell(_ => _.SetProcessToolPath("pwsh").SetCommand($"az account set -s {subscriptionId}"));
        PowerShell(_ => _.SetProcessToolPath("pwsh").SetCommand($"az deployment group create --resource-group {resourceGroupName} --template-file {armTemplatePath}"));
    });

    Target DeployCode => _ => _
    .DependsOn(Pack, DeployInfrastructure)
    .Executes(() =>
    {
        var project = Solution.Projects.First(x => x.Name == "NukeApp");

        var output = GetOutputPath(project);
        var path = output / "NukeApp.zip";

        PowerShell(_ => _.SetProcessToolPath("pwsh").SetCommand($"az webapp deploy -g {ResourceGroup} -n NukeApp --src-path {path} --type zip"));
    });

    private AbsolutePath GetOutputPath(Project project)
    {
        var relativeOutputPath = (RelativePath)"bin";        

        relativeOutputPath = relativeOutputPath / Configuration / "net6.0";
        return project.Directory / relativeOutputPath;
    }

}

