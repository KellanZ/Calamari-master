#if NETCORE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assent;
using Calamari.Aws.Kubernetes.Discovery;
using Calamari.Common.Features.Scripting;
using Calamari.Common.Plumbing.FileSystem;
using Calamari.Deployment;
using Calamari.FeatureToggles;
using Calamari.Kubernetes.ResourceStatus;
using Calamari.Testing;
using Calamari.Testing.Helpers;
using Calamari.Tests.AWS;
using Calamari.Tests.Fixtures.Integration.FileSystem;
using Calamari.Tests.Helpers;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using File = System.IO.File;
using KubernetesSpecialVariables = Calamari.Kubernetes.SpecialVariables;


namespace Calamari.Tests.KubernetesFixtures
{
    [TestFixture]
    [Category(TestCategory.RunOnceOnWindowsAndLinux)]
    public class KubernetesContextScriptWrapperLiveFixtureEks: KubernetesContextScriptWrapperLiveFixture
    {
        string eksClientID;
        string eksSecretKey;
        string eksClusterEndpoint;
        string eksClusterCaCertificate;
        string eksClusterName;
        string awsVpcID;
        string awsSubnetID;
        string awsIamInstanceProfileName;
        string region;
        string eksClusterArn;
        string eksIamRolArn;

        protected override string KubernetesCloudProvider => "EKS";

        protected override async Task PreInitialise()
        {
            region = RegionRandomiser.GetARegion();
            await TestContext.Progress.WriteLineAsync($"Aws Region chosen: {region}");
        }

        protected override async Task InstallOptionalTools(InstallTools tools)
        {
            await tools.InstallAwsAuthenticator();
            await tools.InstallAwsCli();
        }

        protected override IEnumerable<string> ToolsToAddToPath(InstallTools tools)
        {
            return new List<string> { tools.AwsAuthenticatorExecutable, tools.AwsCliExecutable };
        }

        protected override void ExtractVariablesFromTerraformOutput(JObject jsonOutput)
        {
            eksClientID = jsonOutput["eks_client_id"]["value"].Value<string>();
            eksSecretKey = jsonOutput["eks_secret_key"]["value"].Value<string>();
            eksClusterEndpoint = jsonOutput["eks_cluster_endpoint"]["value"].Value<string>();
            eksClusterCaCertificate = jsonOutput["eks_cluster_ca_certificate"]["value"].Value<string>();
            eksClusterName = jsonOutput["eks_cluster_name"]["value"].Value<string>();
            eksClusterArn = jsonOutput["eks_cluster_arn"]["value"].Value<string>();
            eksIamRolArn = jsonOutput["eks_iam_role_arn"]["value"].Value<string>();
            awsVpcID = jsonOutput["aws_vpc_id"]["value"].Value<string>();
            awsSubnetID = jsonOutput["aws_subnet_id"]["value"].Value<string>();
            awsIamInstanceProfileName = jsonOutput["aws_iam_instance_profile_name"]["value"].Value<string>();
        }

        protected override Dictionary<string, string> GetEnvironmentVars()
        {
            return new Dictionary<string, string>
            {
                { "AWS_ACCESS_KEY_ID", ExternalVariables.Get(ExternalVariable.AwsCloudFormationAndS3AccessKey) },
                { "AWS_SECRET_ACCESS_KEY", ExternalVariables.Get(ExternalVariable.AwsCloudFormationAndS3SecretKey) },
                { "AWS_DEFAULT_REGION", region },
                { "TF_VAR_tests_source_dir", testFolder }
            };
        }

        [Test]
        public void DeployRawYaml_WithRawYamlDeploymentScript_OutputShouldIndicateSuccessfulDeployment()
        {
            const string account = "eks_account";
            const string certificateAuthority = "myauthority";
            const string customResourceFileName = "customresource.yml";

            variables.Set(SpecialVariables.Account.AccountType, "AmazonWebServicesAccount");
            variables.Set(KubernetesSpecialVariables.ClusterUrl, eksClusterEndpoint);
            variables.Set(KubernetesSpecialVariables.EksClusterName, eksClusterName);
            variables.Set("Octopus.Action.Aws.Region", region);
            variables.Set("Octopus.Action.AwsAccount.Variable", account);
            variables.Set($"{account}.AccessKey", eksClientID);
            variables.Set($"{account}.SecretKey", eksSecretKey);
            variables.Set("Octopus.Action.Kubernetes.CertificateAuthority", certificateAuthority);
            variables.Set($"{certificateAuthority}.CertificatePem", eksClusterCaCertificate);

            variables.Set("Octopus.Action.Script.ScriptSource", "Inline");
            variables.Set("Octopus.Action.KubernetesContainers.Namespace", "nginx");
            variables.Set("Octopus.Action.Package.JsonConfigurationVariablesTargets", "**/*.{yml,yaml}");
            variables.Set("Octopus.Action.Kubernetes.ResourceStatusCheck", "True");
            variables.Set("Octopus.Action.KubernetesContainers.DeploymentWait", "NoWait");
            variables.Set("Octopus.Action.Kubernetes.DeploymentTimeout", "180");
            variables.Set("Octopus.Action.Kubernetes.StabilizationTimeout", "10");

            variables.SetFeatureToggles(
                FeatureToggle.KubernetesDeploymentStatusFeatureToggle,
                FeatureToggle.MultiGlobPathsForRawYamlFeatureToggle);

            var fileSystem = new TestCalamariPhysicalFileSystem();

            void AddCustomResourceFile(TemporaryDirectory dir)
            {
                var pathToCustomResource = Path.Combine(dir.DirectoryPath, "TestFolder", customResourceFileName);
                File.WriteAllText(pathToCustomResource, "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: nginx-deployment\nspec:\n  selector:\n    matchLabels:\n      app: nginx\n  replicas: 3\n  template:\n    metadata:\n      labels:\n        app: nginx\n    spec:\n      containers:\n      - name: nginx\n        image: nginx:1.14.2\n        ports:\n        - containerPort: 80");
                variables.Set("Octopus.Action.KubernetesContainers.CustomResourceYamlFileName", customResourceFileName);
            }

            var wrapper = new[] { CreateWrapper(fileSystem), CreateK8sResourceStatusReporterScriptWrapper(fileSystem) };

            DeployWithScriptAndVerifySuccess(wrapper, fileSystem, AddCustomResourceFile);

            var rawLogs = Log.Messages.Select(m => m.FormattedMessage).ToArray();

            rawLogs.Should().ContainSingle(m => m.Contains("Deployment/nginx-deployment created"));

            var variableMessages = Log.Messages.GetServiceMessagesOfType("setVariable");

            var variableMessage =
                variableMessages.Should().ContainSingle(m => m.Properties["name"] == "CustomResources(nginx-deployment)")
                                .Subject;

            var scrubbedJson = KubernetesJsonResourceScrubber.ScrubRawJson(variableMessage.Properties["value"], p =>
                p.Name.Contains("Time") ||
                p.Name == "annotations" ||
                p.Name == "uid" ||
                p.Name == "conditions" ||
                p.Name == "resourceVersion");

            this.Assent(scrubbedJson, configuration: AssentConfiguration.Default);

            var idx = Array.IndexOf(rawLogs, "Performing resource status checks on the following resources:");
            rawLogs[idx + 1].Should().Be(" - Deployment/nginx-deployment in namespace calamari-testing");

            var objectStatusUpdates = Log.Messages.GetServiceMessagesOfType("k8s-status");

            objectStatusUpdates.Where(m => m.Properties["status"] == "Successful").Should().HaveCount(5);

            rawLogs.Should().ContainSingle(m =>
                m.Contains("Resource status check completed successfully because all resources are deployed successfully"));
        }

        private IScriptWrapper CreateK8sResourceStatusReporterScriptWrapper(ICalamariFileSystem fileSystem)
        {
            return new ResourceStatusReportWrapper(variables, new ResourceStatusReportExecutor(variables, Log,
                fileSystem,
                new ResourceStatusChecker(new ResourceRetriever(new KubectlGet()),
                    new ResourceUpdateReporter(variables, Log), Log)));
        }

        [Test]
        public void AuthorisingWithAmazonAccount()
        {
            const string account = "eks_account";
            const string certificateAuthority = "myauthority";

            variables.Set(SpecialVariables.Account.AccountType, "AmazonWebServicesAccount");
            variables.Set(KubernetesSpecialVariables.ClusterUrl, eksClusterEndpoint);
            variables.Set(KubernetesSpecialVariables.EksClusterName, eksClusterName);
            variables.Set("Octopus.Action.Aws.Region", region);
            variables.Set("Octopus.Action.AwsAccount.Variable", account);
            variables.Set($"{account}.AccessKey", eksClientID);
            variables.Set($"{account}.SecretKey", eksSecretKey);
            variables.Set("Octopus.Action.Kubernetes.CertificateAuthority", certificateAuthority);
            variables.Set($"{certificateAuthority}.CertificatePem", eksClusterCaCertificate);
            var wrapper = CreateWrapper();

            // When authorising via AWS, We need to make sure we are using the correct version of
            // kubectl for the test script as newer versions may cause kubectl to fail with an error like:
            // 'error: exec plugin: invalid apiVersion "client.authentication.k8s.io/v1alpha1"'
            var kubectlExecutable = variables.Get(KubeCtlExecutableVariableName) ??
                throw new Exception($"Unable to find required kubectl executable in variable '{KubeCtlExecutableVariableName}'");

            TestScriptAndVerifyCluster(wrapper, "Test-Script", kubectlExecutable);
        }

        [Test]
        public void UnreachableK8Cluster_ShouldExecuteTargetScript()
        {
            const string account = "eks_account";
            const string certificateAuthority = "myauthority";
            const string unreachableClusterEndpoint = "https://example.kubernetes.com";

            variables.Set(Deployment.SpecialVariables.Account.AccountType, "AmazonWebServicesAccount");
            variables.Set(KubernetesSpecialVariables.ClusterUrl, unreachableClusterEndpoint);
            variables.Set(KubernetesSpecialVariables.EksClusterName, eksClusterName);
            variables.Set("Octopus.Action.Aws.Region", region);
            variables.Set("Octopus.Action.AwsAccount.Variable", account);
            variables.Set($"{account}.AccessKey", eksClientID);
            variables.Set($"{account}.SecretKey", eksSecretKey);
            variables.Set("Octopus.Action.Kubernetes.CertificateAuthority", certificateAuthority);
            variables.Set($"{certificateAuthority}.CertificatePem", eksClusterCaCertificate);
            var wrapper = CreateWrapper();

            TestScript(wrapper, "Test-Script");
        }

        [Test]
        public void UsingEc2Instance()
        {
            var terraformWorkingFolder = InitialiseTerraformWorkingFolder("terraform_working/EC2", "KubernetesFixtures/Terraform/EC2");

            var env = new Dictionary<string, string>
            {
                { "TF_VAR_iam_role_arn", eksIamRolArn },
                { "TF_VAR_cluster_name", eksClusterName },
                { "TF_VAR_aws_vpc_id", awsVpcID },
                { "TF_VAR_aws_subnet_id", awsSubnetID },
                { "TF_VAR_aws_iam_instance_profile_name", awsIamInstanceProfileName },
                { "TF_VAR_aws_region", region }
            };

            RunTerraformInternal(terraformWorkingFolder, env, "init");
            try
            {
                // This actual tests are run via EC2/test.sh which executes the tests in
                // KubernetesContextScriptWrapperLiveFixtureForAmazon.cs
                RunTerraformInternal(terraformWorkingFolder, env, "apply", "-auto-approve");
            }
            finally
            {
                RunTerraformDestroy(terraformWorkingFolder, env);
            }
        }

        [Test]
        public void DiscoverKubernetesClusterWithEnvironmentVariableCredentialsAndIamRole()
        {
            const string accessKeyEnvVar = "AWS_ACCESS_KEY_ID";
            const string secretKeyEnvVar = "AWS_SECRET_ACCESS_KEY";
            var originalAccessKey = Environment.GetEnvironmentVariable(accessKeyEnvVar);
            var originalSecretKey = Environment.GetEnvironmentVariable(secretKeyEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(accessKeyEnvVar, eksClientID);
                Environment.SetEnvironmentVariable(secretKeyEnvVar, eksSecretKey);

                var authenticationDetails = new AwsAuthenticationDetails
                {
                    Type = "Aws",
                    Credentials = new AwsCredentials { Type = "worker" },
                    Role = new AwsAssumedRole
                    {
                        Type = "assumeRole",
                        Arn = eksIamRolArn
                    },
                    Regions = new []{region}
                };

                var serviceMessageProperties = new Dictionary<string, string>
                    {
                        { "name", eksClusterArn },
                        { "clusterName", eksClusterName },
                        { "clusterUrl", eksClusterEndpoint },
                        { "skipTlsVerification", bool.TrueString },
                        { "octopusDefaultWorkerPoolIdOrName", "WorkerPools-1" },
                        { "octopusRoles", "discovery-role" },
                        { "updateIfExisting", bool.TrueString },
                        { "isDynamic", bool.TrueString },
                        { "awsUseWorkerCredentials", bool.TrueString },
                        { "awsAssumeRole", bool.TrueString },
                        { "awsAssumeRoleArn", eksIamRolArn },
                    };

                DoDiscoveryAndAssertReceivedServiceMessageWithMatchingProperties(authenticationDetails,
                    serviceMessageProperties);
            }
            finally
            {
                Environment.SetEnvironmentVariable(accessKeyEnvVar, originalAccessKey);
                Environment.SetEnvironmentVariable(secretKeyEnvVar, originalSecretKey);
            }
        }

        [Test]
        public void DiscoverKubernetesClusterWithEnvironmentVariableCredentialsAndNoIamRole()
        {
            const string accessKeyEnvVar = "AWS_ACCESS_KEY_ID";
            const string secretKeyEnvVar = "AWS_SECRET_ACCESS_KEY";
            var originalAccessKey = Environment.GetEnvironmentVariable(accessKeyEnvVar);
            var originalSecretKey = Environment.GetEnvironmentVariable(secretKeyEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(accessKeyEnvVar, eksClientID);
                Environment.SetEnvironmentVariable(secretKeyEnvVar, eksSecretKey);

                var authenticationDetails = new AwsAuthenticationDetails
                {
                    Type = "Aws",
                    Credentials = new AwsCredentials { Type = "worker" },
                    Role = new AwsAssumedRole { Type = "noAssumedRole" },
                    Regions = new []{region}
                };

                var serviceMessageProperties = new Dictionary<string, string>
                    {
                        { "name", eksClusterArn },
                        { "clusterName", eksClusterName },
                        { "clusterUrl", eksClusterEndpoint },
                        { "skipTlsVerification", bool.TrueString },
                        { "octopusDefaultWorkerPoolIdOrName", "WorkerPools-1" },
                        { "octopusRoles", "discovery-role" },
                        { "updateIfExisting", bool.TrueString },
                        { "isDynamic", bool.TrueString },
                        { "awsUseWorkerCredentials", bool.TrueString },
                        { "awsAssumeRole", bool.FalseString }
                    };

                DoDiscoveryAndAssertReceivedServiceMessageWithMatchingProperties(authenticationDetails,
                    serviceMessageProperties);
            }
            finally
            {
                Environment.SetEnvironmentVariable(accessKeyEnvVar, originalAccessKey);
                Environment.SetEnvironmentVariable(secretKeyEnvVar, originalSecretKey);
            }
        }

        [Test]
        public void DiscoverKubernetesClusterWithAwsAccountCredentialsAndNoIamRole()
        {
            var authenticationDetails = new AwsAuthenticationDetails
            {
                Type = "Aws",
                Credentials = new AwsCredentials
                {
                    Account = new AwsAccount
                    {
                        AccessKey = eksClientID,
                        SecretKey = eksSecretKey
                    },
                    AccountId = "Accounts-1",
                    Type = "account"
                },
                Role = new AwsAssumedRole { Type = "noAssumedRole" },
                Regions = new []{region}
            };

            var serviceMessageProperties = new Dictionary<string, string>
            {
                { "name", eksClusterArn },
                { "clusterName", eksClusterName },
                { "clusterUrl", eksClusterEndpoint },
                { "skipTlsVerification", bool.TrueString },
                { "octopusDefaultWorkerPoolIdOrName", "WorkerPools-1" },
                { "octopusAccountIdOrName", "Accounts-1" },
                { "octopusRoles", "discovery-role" },
                { "updateIfExisting", bool.TrueString },
                { "isDynamic", bool.TrueString },
                { "awsUseWorkerCredentials", bool.FalseString },
                { "awsAssumeRole", bool.FalseString }
            };

            DoDiscoveryAndAssertReceivedServiceMessageWithMatchingProperties(authenticationDetails, serviceMessageProperties);
        }

        [Test]
        public void DiscoverKubernetesClusterWithAwsAccountCredentialsAndIamRole()
        {
            const int sessionDuration = 900;
            var authenticationDetails = new AwsAuthenticationDetails
            {
                Type = "Aws",
                Credentials = new AwsCredentials
                {
                    Account = new AwsAccount
                    {
                        AccessKey = eksClientID,
                        SecretKey = eksSecretKey
                    },
                    AccountId = "Accounts-1",
                    Type = "account"
                },
                Role = new AwsAssumedRole
                {
                    Type = "assumeRole",
                    Arn = eksIamRolArn,
                    SessionName = "ThisIsASessionName",
                    SessionDuration = sessionDuration
                },
                Regions = new []{region}
            };

            var serviceMessageProperties = new Dictionary<string, string>
            {
                { "name", eksClusterArn },
                { "clusterName", eksClusterName },
                { "clusterUrl", eksClusterEndpoint },
                { "skipTlsVerification", bool.TrueString },
                { "octopusDefaultWorkerPoolIdOrName", "WorkerPools-1" },
                { "octopusAccountIdOrName", "Accounts-1" },
                { "octopusRoles", "discovery-role" },
                { "updateIfExisting", bool.TrueString },
                { "isDynamic", bool.TrueString },
                { "awsUseWorkerCredentials", bool.FalseString },
                { "awsAssumeRole", bool.TrueString },
                { "awsAssumeRoleArn", eksIamRolArn },
                { "awsAssumeRoleSession", "ThisIsASessionName" },
                { "awsAssumeRoleSessionDurationSeconds", sessionDuration.ToString() }
            };

            DoDiscoveryAndAssertReceivedServiceMessageWithMatchingProperties(authenticationDetails, serviceMessageProperties);
        }

        [Test]
        public void DiscoverKubernetesClusterWithNoValidCredentials()
        {
            const string accessKeyEnvVar = "AWS_ACCESS_KEY_ID";
            const string secretKeyEnvVar = "AWS_SECRET_ACCESS_KEY";
            var originalAccessKey = Environment.GetEnvironmentVariable(accessKeyEnvVar);
            var originalSecretKey = Environment.GetEnvironmentVariable(secretKeyEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(accessKeyEnvVar, null);
                Environment.SetEnvironmentVariable(secretKeyEnvVar, null);

                var authenticationDetails = new AwsAuthenticationDetails
                {
                    Type = "Aws",
                    Credentials = new AwsCredentials { Type = "worker" },
                    Role = new AwsAssumedRole { Type = "noAssumedRole" },
                    Regions = new []{region}
                };

                DoDiscovery(authenticationDetails);

                Log.ServiceMessages.Should().BeEmpty();

                Log.Messages.Should().NotContain(m => m.Level == InMemoryLog.Level.Error);

                Log.StandardError.Should().BeEmpty();

                Log.Messages.Should()
                    .ContainSingle(m =>
                        m.Level == InMemoryLog.Level.Warn &&
                        m.FormattedMessage ==
                        "Unable to authorise credentials, see verbose log for details.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(accessKeyEnvVar, originalAccessKey);
                Environment.SetEnvironmentVariable(secretKeyEnvVar, originalSecretKey);
            }
        }

        [Test]
        public void DiscoverKubernetesClusterWithInvalidAccountCredentials()
        {
            var authenticationDetails = new AwsAuthenticationDetails
            {
                Type = "Aws",
                Credentials = new AwsCredentials
                {
                    Account = new AwsAccount
                    {
                        AccessKey = "abcdefg",
                        SecretKey = null
                    },
                    AccountId = "Accounts-1",
                    Type = "account"
                },
                Role = new AwsAssumedRole { Type = "noAssumedRole" },
                Regions = new []{region}
            };

            DoDiscovery(authenticationDetails);

            Log.ServiceMessages.Should().BeEmpty();

            Log.Messages.Should().NotContain(m => m.Level == InMemoryLog.Level.Error);

            Log.StandardError.Should().BeEmpty();

            Log.Messages.Should()
                .ContainSingle(m =>
                    m.Level == InMemoryLog.Level.Warn &&
                    m.FormattedMessage ==
                    "Unable to authorise credentials, see verbose log for details.");
        }
    }
}
#endif