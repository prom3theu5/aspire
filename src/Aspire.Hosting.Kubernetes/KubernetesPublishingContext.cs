// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Kubernetes.Yaml;
using Aspire.Hosting.Yaml;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.Kubernetes;

internal class KubernetesPublishingContext(
    DistributedApplicationExecutionContext executionContext,
    KubernetesPublisherOptions publisherOptions,
    ILogger logger,
    CancellationToken cancellationToken = default)
{
    private const string TemplateFileSeparator = "---";
    private const string ParametersKey = "parameters";
    private readonly Dictionary<IResource, KubernetesComponentContext> _kubernetesComponents = [];
    private readonly Dictionary<string, object> _helmValues = new()
    {
        [ParametersKey] = new Dictionary<string, object>(),
    };

    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ByteArrayStringYamlConverter())
        .WithEventEmitter(nextEmitter => new ForceQuotedStringsEventEmitter(nextEmitter))
        .WithEventEmitter(e => new FloatEmitter(e))
        .WithEmissionPhaseObjectGraphVisitor(args => new YamlIEnumerableSkipEmptyObjectGraphVisitor(args.InnerVisitor))
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithNewLine("\n")
        .WithIndentedSequences()
        .Build();

    private ILogger Logger => logger;

    internal async Task WriteModelAsync(DistributedApplicationModel model)
    {
        if (!executionContext.IsPublishMode)
        {
            logger.NotInPublishingMode();
            return;
        }

        logger.StartGeneratingKubernetes();

        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(publisherOptions.OutputPath);

        if (model.Resources.Count == 0)
        {
            logger.EmptyModel();
            return;
        }

        await WriteKubernetesOutputAsync(model).ConfigureAwait(false);

        logger.FinishGeneratingKubernetes(publisherOptions.OutputPath);
    }

    private async Task WriteKubernetesOutputAsync(DistributedApplicationModel model)
    {
        foreach (var resource in model.Resources)
        {
            if (resource.TryGetLastAnnotation<ManifestPublishingCallbackAnnotation>(out var lastAnnotation) &&
                lastAnnotation == ManifestPublishingCallbackAnnotation.Ignore)
            {
                continue;
            }

            if (!resource.IsContainer() && resource is not ProjectResource)
            {
                continue;
            }

            var kubernetesComponentContext = await ProcessResourceAsync(resource).ConfigureAwait(false);
            kubernetesComponentContext.BuildKubernetesResources();

            await WriteKubernetesTemplatesForResource(resource, kubernetesComponentContext.TemplatedResources).ConfigureAwait(false);
            AppendResourceContextToHelmValues(resource, kubernetesComponentContext);
        }

        await WriteKubernetesHelmChartAsync().ConfigureAwait(false);
        await WriteKubernetesHelmValuesAsync().ConfigureAwait(false);
    }

    private void AppendResourceContextToHelmValues(IResource resource, KubernetesComponentContext resourceContext)
    {
        if (_helmValues[ParametersKey] is Dictionary<string, object> helmParameters)
        {
            if (resourceContext.Parameters.Count == 0)
            {
                return;
            }

            helmParameters[resource.Name] = resourceContext.Parameters;
        }
    }

    private async Task WriteKubernetesTemplatesForResource(IResource resource, List<BaseKubernetesResource> templatedItems)
    {
        var templatesFolder = Path.Combine(publisherOptions.OutputPath!, "templates", resource.Name);
        Directory.CreateDirectory(templatesFolder);

        foreach (var templatedItem in templatedItems)
        {
            var fileName = $"{templatedItem.GetType().Name.ToLowerInvariant()}.yaml";
            var outputFile = Path.Combine(templatesFolder, fileName);
            var yaml = _serializer.Serialize(templatedItem);

            using var writer = new StreamWriter(outputFile);
            await writer.WriteLineAsync(TemplateFileSeparator).ConfigureAwait(false);
            await writer.WriteAsync(yaml).ConfigureAwait(false);
        }
    }

    private async Task WriteKubernetesHelmValuesAsync()
    {
        var valuesYaml = _serializer.Serialize(_helmValues);
        var outputFile = Path.Combine(publisherOptions.OutputPath!, "values.yaml");
        Directory.CreateDirectory(publisherOptions.OutputPath!);
        await File.WriteAllTextAsync(outputFile, valuesYaml, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteKubernetesHelmChartAsync()
    {
        var helmChart = new HelmChart
        {
            Name = "aspire",
            Version = "0.1.0",
            AppVersion = "0.1.0",
            Type = "application",
            ApiVersion = "v2",
            Description = "Aspire Helm Chart",
            Keywords = ["aspire", "kubernetes"],
            KubeVersion = ">= 1.18.0-0",
        };

        var chartYaml = _serializer.Serialize(helmChart);
        var outputFile = Path.Combine(publisherOptions.OutputPath!, "Chart.yaml");
        Directory.CreateDirectory(publisherOptions.OutputPath!);
        await File.WriteAllTextAsync(outputFile, chartYaml, cancellationToken).ConfigureAwait(false);
    }

    private async Task<KubernetesComponentContext> ProcessResourceAsync(IResource resource)
    {
        if (!_kubernetesComponents.TryGetValue(resource, out var context))
        {
            _kubernetesComponents[resource] = context = new(resource, this);
            await context.ProcessResourceAsync(executionContext, cancellationToken).ConfigureAwait(false);
        }

        return context;
    }

    private sealed class KubernetesComponentContext(IResource resource, KubernetesPublishingContext kubernetesPublishingContext)
    {
        private record struct EndpointMapping(string Scheme, string Host, int InternalPort, int ExposedPort, bool IsHttpIngress);
        private readonly Dictionary<string, EndpointMapping> _endpointMapping = [];
        public readonly Dictionary<string, string?> EnvironmentVariables = [];
        public readonly Dictionary<string, string?> Secrets = [];
        public readonly Dictionary<string, string> Parameters = [];
        private Dictionary<string, string> _labels = [];

        public List<BaseKubernetesResource> TemplatedResources { get; } = [];
        private List<string> Commands { get; } = [];
        public List<VolumeMountV1> Volumes { get; } = [];

        public void BuildKubernetesResources()
        {
            SetLabels();
            CreateConfigMap();
            CreateSecret();
            CreateStatefulSetResource();
            CreateDeploymentResource();
        }

        private void SetLabels()
        {
            _labels = new()
            {
                ["app"] = "aspire",
                ["component"] = resource.Name,
            };
        }

        private void CreateDeploymentResource()
        {
            if (resource is IResourceWithConnectionString)
            {
                return;
            }

            var deployment = new Deployment
            {
                Metadata =
                {
                    Name = CurrentResourceDeploymentName,
                },
                Spec =
                {
                    Selector = new(_labels.ToDictionary()),
                    Replicas = resource.GetReplicaCount(),
                    Template = CreatePodSpec(),
                    Strategy = new()
                    {
                        Type = "RollingUpdate",
                        RollingUpdate = new()
                        {
                            MaxUnavailable = 1,
                            MaxSurge = 1,
                        },
                    },
                },
            };

            TemplatedResources.Add(deployment);
        }

        private void CreateStatefulSetResource()
        {
            if (resource is not IResourceWithConnectionString)
            {
                return;
            }

            var statefulSet = new StatefulSet
            {
                Metadata =
                {
                    Name = CurrentResourceStatefulSetName,
                },
                Spec =
                {
                    Selector = new(_labels.ToDictionary()),
                    Replicas = resource.GetReplicaCount(),
                    Template = CreatePodSpec(),
                },
            };

            TemplatedResources.Add(statefulSet);
        }

        private PodTemplateSpecV1 CreatePodSpec()
        {
            var podTemplateSpec = new PodTemplateSpecV1
            {
                Metadata =
                {
                    Labels = _labels.ToDictionary(),
                },
                Spec =
                {
                    Containers =
                    {
                        ConfigureContainerForPod(),
                    },
                },
            };

            SetPodSpecVolumes(podTemplateSpec.Spec);

            return podTemplateSpec;
        }

        private ContainerV1 ConfigureContainerForPod()
        {
            var container = new ContainerV1
            {
                Name = resource.Name,
                ImagePullPolicy = "IfNotPresent",
            };

            SetContainerImage(container);
            SetContainerPorts(container);
            SetContainerVolumes(container);
            SetContainerEntrypoint(container);
            SetContainerArgs(container);
            SetContainerEnv(container);

            return container;
        }

        private void SetContainerVolumes(ContainerV1 container)
        {
            if (Volumes.Count == 0)
            {
                return;
            }

            foreach (var volume in Volumes)
            {
                container.VolumeMounts.Add(new()
                {
                    Name = volume.Name,
                });
            }
        }

        private void SetPodSpecVolumes(PodSpecV1 podSpec)
        {
            if (Volumes.Count == 0)
            {
                return;
            }

            foreach (var volume in Volumes)
            {
                podSpec.Volumes.Add(new()
                {
                    Name = volume.Name,
                    HostPath = new()
                    {
                        Path = volume.MountPath,
                        Type = "Directory",
                    },
                });
            }
        }

        private void SetContainerPorts(ContainerV1 container)
        {
            if (_endpointMapping.Count == 0)
            {
                return;
            }

            foreach (var (_, (scheme, _, internalPort, exposedPort, _)) in _endpointMapping)
            {
                container.Ports.Add(new()
                {
                    Name = scheme,
                    ContainerPort = internalPort,
                    HostPort = exposedPort,
                    Protocol = scheme.ToUpperInvariant(),
                });
            }
        }

        private void SetContainerImage(ContainerV1 container)
        {
            if (!TryGetContainerImageName(resource, out var containerImageName))
            {
                kubernetesPublishingContext.Logger.FailedToGetContainerImage(resource.Name);
            }

            if (containerImageName is not null)
            {
                container.Image = containerImageName;
            }
        }

        private void SetContainerEntrypoint(ContainerV1 container)
        {
            if (resource is ContainerResource { Entrypoint: { } entrypoint })
            {
                container.Command.Add(entrypoint);
            }
        }

        private void SetContainerArgs(ContainerV1 container)
        {
            if (Commands.Count == 0)
            {
                return;
            }

            foreach (var command in Commands)
            {
                container.Args.Add(command);
            }
        }

        private void SetContainerEnv(ContainerV1 container)
        {
            if (EnvironmentVariables.Count > 0)
            {
                container.EnvFrom.Add(new()
                {
                    ConfigMapRef = new()
                    {
                        Name = CurrentResourceConfigMapName,
                    },
                });
            }

            if (Secrets.Count > 0)
            {
                container.EnvFrom.Add(new()
                {
                    SecretRef = new()
                    {
                        Name = CurrentResourceSecretName,
                    },
                });
            }
        }

        private void CreateSecret()
        {
            if (Secrets.Count == 0)
            {
                return;
            }

            var secret = new Secret
            {
                Metadata =
                {
                    Name = CurrentResourceSecretName,
                    Labels =
                    {
                        ["app"] = "aspire",
                        ["component"] = resource.Name,
                    },
                },
            };

            foreach (var kvp in Secrets)
            {
                secret.StringData[kvp.Key] = kvp.Value ?? GetParameterExpression(kvp.Key);
            }

            TemplatedResources.Add(secret);
        }

        private void CreateConfigMap()
        {
            if (EnvironmentVariables.Count == 0)
            {
                return;
            }

            var configMap = new ConfigMap
            {
                Metadata =
                {
                    Name = CurrentResourceConfigMapName,
                    Labels =
                    {
                        ["app"] = "aspire",
                        ["component"] = resource.Name,
                    },
                },
            };

            foreach (var kvp in EnvironmentVariables)
            {
                configMap.Data[kvp.Key] = kvp.Value ?? GetParameterExpression(kvp.Key);
            }

            TemplatedResources.Add(configMap);
        }

        private bool TryGetContainerImageName(IResource resourceInstance, out string? containerImageName)
        {
            if (resourceInstance.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _) || resourceInstance is ProjectResource)
            {
                var imageEnvName = $"{resourceInstance.Name.ToUpperInvariant().Replace("-", "_")}_IMAGE";

                Parameters[imageEnvName] = $"{resourceInstance.Name}:latest";

                containerImageName = GetParameterExpression(imageEnvName);
                return false;
            }

            return resourceInstance.TryGetContainerImageName(out containerImageName);
        }

        public async Task ProcessResourceAsync(DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            ProcessEndpoints();

            await ProcessEnvironmentAsync(executionContext, cancellationToken).ConfigureAwait(false);
            await ProcessArgumentsAsync(cancellationToken).ConfigureAwait(false);
        }

        private void ProcessEndpoints()
        {
            if (!resource.TryGetEndpoints(out var endpoints))
            {
                return;
            }

            foreach (var endpoint in endpoints)
            {
                var internalPort = endpoint.TargetPort ?? 80;
                var exposedPort = endpoint.TargetPort ?? 80;

                _endpointMapping[endpoint.Name] = new(endpoint.UriScheme, resource.Name, internalPort, exposedPort, false);
            }
        }

        private async Task ProcessArgumentsAsync(CancellationToken cancellationToken)
        {
            if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var commandLineArgsCallbackAnnotations))
            {
                var context = new CommandLineArgsCallbackContext([], cancellationToken: cancellationToken);

                foreach (var c in commandLineArgsCallbackAnnotations)
                {
                    await c.Callback(context).ConfigureAwait(false);
                }

                foreach (var arg in context.Args)
                {
                    var value = await ProcessValueAsync(arg).ConfigureAwait(false);

                    if (value is not string str)
                    {
                        throw new NotSupportedException("Command line args must be strings");
                    }

                    Commands.Add(new(str));
                }
            }
        }

        private async Task ProcessEnvironmentAsync(DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var environmentCallbacks))
            {
                var context = new EnvironmentCallbackContext(executionContext, cancellationToken: cancellationToken);

                foreach (var c in environmentCallbacks)
                {
                    await c.Callback(context).ConfigureAwait(false);
                }

                foreach (var kv in context.EnvironmentVariables)
                {
                    var value = await ProcessValueAsync(kv.Value).ConfigureAwait(false);

                    EnvironmentVariables[kv.Key] = value.ToString() ?? string.Empty;
                }
            }
        }

        private static string GetValue(EndpointMapping mapping, EndpointProperty property)
        {
            var (scheme, host, internalPort, exposedPort, isHttpIngress) = mapping;

            return property switch
            {
                EndpointProperty.Url => GetHostValue($"{scheme}://", suffix: isHttpIngress ? null : $":{internalPort}"),
                EndpointProperty.Host or EndpointProperty.IPV4Host => GetHostValue(),
                EndpointProperty.Port => internalPort.ToString(CultureInfo.InvariantCulture),
                EndpointProperty.HostAndPort => GetHostValue(suffix: $":{internalPort}"),
                EndpointProperty.TargetPort => $"{exposedPort}",
                EndpointProperty.Scheme => scheme,
                _ => throw new NotSupportedException(),
            };

            string GetHostValue(string? prefix = null, string? suffix = null)
            {
                return $"{prefix}{host}{suffix}";
            }
        }

        private async Task<object> ProcessValueAsync(object value)
        {
            while (true)
            {
                if (value is string s)
                {
                    return s;
                }

                if (value is EndpointReference ep)
                {
                    var context = ep.Resource == resource
                        ? this
                        : await kubernetesPublishingContext.ProcessResourceAsync(ep.Resource)
                            .ConfigureAwait(false);

                    var mapping = context._endpointMapping[ep.EndpointName];

                    var url = GetValue(mapping, EndpointProperty.Url);

                    return url;
                }

                if (value is ParameterResource param)
                {
                    return AllocateParameter(param);
                }
                if (value is ConnectionStringReference cs)
                {
                    value = cs.Resource.ConnectionStringExpression;
                    continue;
                }

                if (value is IResourceWithConnectionString csrs)
                {
                    value = csrs.ConnectionStringExpression;
                    continue;
                }

                if (value is EndpointReferenceExpression epExpr)
                {
                    var context = epExpr.Endpoint.Resource == resource
                        ? this
                        : await kubernetesPublishingContext.ProcessResourceAsync(epExpr.Endpoint.Resource).ConfigureAwait(false);

                    var mapping = context._endpointMapping[epExpr.Endpoint.EndpointName];

                    var val = GetValue(mapping, epExpr.Property);

                    return val;
                }

                if (value is ReferenceExpression expr)
                {
                    if (expr is { Format: "{0}", ValueProviders.Count: 1 })
                    {
                        return (await ProcessValueAsync(expr.ValueProviders[0]).ConfigureAwait(false)).ToString() ?? string.Empty;
                    }

                    var args = new object[expr.ValueProviders.Count];
                    var index = 0;

                    foreach (var vp in expr.ValueProviders)
                    {
                        var val = await ProcessValueAsync(vp).ConfigureAwait(false);
                        args[index++] = val ?? throw new InvalidOperationException("Value is null");
                    }

                    return string.Format(CultureInfo.InvariantCulture, expr.Format, args);
                }

                // If we don't know how to process the value, we just return it as an external reference
                if (value is IManifestExpressionProvider r)
                {
                    kubernetesPublishingContext.Logger.NotSupportedResourceWarning(nameof(value), r.GetType().Name);

                    return ResolveUnknownValue(r);
                }

                return value; // todo: we need to never get here really...
            }
        }

        private string AllocateParameter(ParameterResource parameter)
        {
            var formattedName = parameter.Name.ToUpperInvariant().Replace("-", "_");

            if (parameter.Secret)
            {
                Secrets[formattedName] = parameter.Default is null ? null : parameter.Value;
            }
            else
            {
                EnvironmentVariables[formattedName] = parameter.Default is null ? null : parameter.Value;
            }

            return GetParameterExpression(formattedName);
        }

        private string ResolveUnknownValue(IManifestExpressionProvider parameter)
        {
            var formattedName = parameter.ValueExpression.Replace("{", "")
                     .Replace("}", "")
                     .Replace(".", "_")
                     .Replace("-", "_")
                     .ToUpperInvariant();

            EnvironmentVariables[formattedName] = parameter.ValueExpression;

            return GetParameterExpression(formattedName);
        }
        private string CurrentResourceConfigMapName => $"{resource.Name}-config";
        private string CurrentResourceSecretName => $"{resource.Name}-secret";
        private string CurrentResourceDeploymentName => $"{resource.Name}-deployment";
        private string CurrentResourceStatefulSetName => $"{resource.Name}-statefulset";
        private string GetParameterExpression(string parameterName) => $"{{{{ .Values.{ParametersKey}.{resource.Name}.{parameterName} }}}}";
    }
}
