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

[GitHubActions(
    "continuous",
    GitHubActionsImage.UbuntuLatest,
    On = new[] { GitHubActionsTrigger.Push },    
    InvokedTargets = new[] { nameof(Pack), nameof(Compile), nameof(DeployInfrastructure) })]
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Pack, x => x.UnitTest);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution("./NukeApp/NukeApp.sln")]
    readonly Solution Solution;

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
            Log.Information($"Output: {output}");
            CompressZip(output, output / "NukeApp.zip", compressionLevel: CompressionLevel.SmallestSize, fileMode: FileMode.CreateNew);

            

        });

    Target ServerAuthentication => _ => _        
        .Executes(() =>
        {
            if (IsServerBuild)
            {
                var appId = "f74f221f-4794-401f-8a44-73e2cd457adb";
                var secret = "GqA8Q~J-g0kuyvVqmFCavF1Tgu6GPIj4FltwodAO";
                var tenantId = "51f2b856-c214-467f-b811-ebe0e9c4092f";

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
        var resourceGroupName = "github-actions";
        var subscriptionId = "4883b89e-964b-4799-8d8f-bdf71e856a4d";

        PowerShell($"az account set -s {subscriptionId}");
        PowerShell($"az deployment group create --resource-group {resourceGroupName} --template-file {RootDirectory/"arm"/"template.json"}");
    });

    Target DeployCode => _ => _
    .DependsOn(Compile)
    .Executes(async () =>
    {
        var project = Solution.Projects.First(x => x.Name == "NukeApp");

        var armClient = new ArmClient(new DefaultAzureCredential());

        var subscription = armClient.GetSubscriptionResource(new ResourceIdentifier("/subscriptions/4883b89e-964b-4799-8d8f-bdf71e856a4d"));
        var resourceGroup = await subscription.GetResourceGroups().GetAsync("github-actions");
        

    });

    private AbsolutePath GetOutputPath(Project project)
    {
        var relativeOutputPath = (RelativePath)"bin";        

        relativeOutputPath = relativeOutputPath / Configuration / "net6.0";
        return project.Directory / relativeOutputPath;
    }

}

