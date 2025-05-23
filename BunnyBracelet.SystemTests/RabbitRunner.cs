﻿using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;

using ContainerStatus = (string? id, bool isRunning, bool isConnected);

namespace BunnyBracelet.SystemTests;

/// <summary>
/// This object controls an instance of RabbitMQ Docker container.
/// It manages the lifetime and also provides operations to simulate
/// network disruption.
/// </summary>
/// <remarks>
/// RabbitMQ containers are reused in the same environment identified
/// by <see cref="EnvironmentId"/>. Starting of a RabbitMQ container takes
/// approx. 10 seconds. Therefore, reusing of the containers in all tests
/// reduces time of the tests. The environment should be reset and cleaned
/// after end of a test set (<see cref="ClassCleanupAttribute"/>).
/// </remarks>
internal sealed class RabbitRunner : IDisposable
{
    private const string Image = "rabbitmq:4.1";
    private const int RabbitMQPort = 5672;

    private const string ContainerNamePrefix = "rabbitmq";
    private const string EnvironmentLabel = "BunnyBracelet-Environment";
    private const string PortLabel = "BunnyBracelet-Port";
    private const string NetworkName = "bridge";
    private const string ConfigurationFileName = "BunnyTest.conf";
    private const string ContainerConfigurationPath = "/etc/rabbitmq/conf.d/BunnyTest.conf";

    // Increase maximum message size for TooLargeMessage test
    private const string Configuration = "max_message_size = 33554432";

    private static readonly SemaphoreSlim PullImageSemaphore = new(1, 1);
    private static readonly Dictionary<int, string> EnvironmentPasswords = [];
    private static readonly Lazy<string> ConfigurationPathProvider = new(GetConfigurationPath);

    private readonly Lazy<DockerClient> dockerClient = new(CreateDockerClient);
    private readonly string containerName;
    private string? containerId;

    public RabbitRunner()
        : this(RabbitMQPort)
    {
    }

    public RabbitRunner(int port)
    {
        Port = port;
        Username = "bunny";

        if (EnvironmentPasswords.TryGetValue(port, out var password))
        {
            Password = password;
        }
        else
        {
            Password = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            EnvironmentPasswords[port] = Password;
        }

        containerName = $"{ContainerNamePrefix}-{EnvironmentId}-{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string EnvironmentId { get; private set; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public static string ConfigurationPath => ConfigurationPathProvider.Value;

    public int Port { get; }

    public string Username { get; }

    public string Password { get; }

    public string Uri => $"amqp://{Username}:{Password}@localhost:{Port.ToString(CultureInfo.InvariantCulture)}/";

    public static void Reset()
    {
        EnvironmentId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        EnvironmentPasswords.Clear();
    }

    public async Task Start()
    {
        if (containerId is null)
        {
            await PullImage();

            var containerStatus = await TryGetContainer();
            if (containerStatus.id is null)
            {
                containerStatus = await CreateContainer();
            }

            containerId = containerStatus.id;

            if (!containerStatus.isConnected)
            {
                await ConnectContainerToNetwork();
            }

            if (!containerStatus.isRunning)
            {
                await StartContainer();
            }
        }
    }

    public async Task Stop()
    {
        if (containerId is not null)
        {
            await StopContainer();
            await dockerClient.Value.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters());
        }
    }

    /// <summary>
    /// Removes all containers, which have BunnyBracelet-Environment label,
    /// but the value is different from current <see cref="EnvironmentId"/>.
    /// </summary>
    public async Task Cleanup()
    {
        var filters = new Dictionary<string, string>()
        {
            { "name", ContainerNamePrefix },
            { "label", EnvironmentLabel }
        };
        var listParameters = new ContainersListParameters
        {
            All = true,
            Filters = CreateFilters(filters)
        };
        var containers = await dockerClient.Value.Containers.ListContainersAsync(listParameters);

        foreach (var container in containers)
        {
            var isInactiveEnvironment = container.Labels.TryGetValue(EnvironmentLabel, out var label) &&
                label != EnvironmentId;
            if (isInactiveEnvironment)
            {
                await dockerClient.Value.Containers.StopContainerAsync(container.ID, new ContainerStopParameters());
                await dockerClient.Value.Containers.RemoveContainerAsync(container.ID, new ContainerRemoveParameters());
            }
        }
    }

    public async Task ConnectContainerToNetwork()
    {
        var networkParameters = new NetworkConnectParameters
        {
            Container = containerName
        };
        await dockerClient.Value.Networks.ConnectNetworkAsync(NetworkName, networkParameters);
    }

    public async Task DisconnectContainerFromNetwork()
    {
        var networkParameters = new NetworkDisconnectParameters
        {
            Container = containerName
        };
        await dockerClient.Value.Networks.DisconnectNetworkAsync(NetworkName, networkParameters);
    }

    public async Task StartContainer()
    {
        var startTime = DateTimeOffset.UtcNow;
        await dockerClient.Value.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
        if (!await WaitForInitialization(startTime))
        {
            throw new InvalidOperationException("RabbitMQ initialization failed.");
        }
    }

    public async Task StopContainer()
    {
        await dockerClient.Value.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
    }

    public RabbitConnection CreateConnection() => new(Uri);

    public void Dispose()
    {
        if (dockerClient.IsValueCreated)
        {
            dockerClient.Value.Dispose();
        }
    }

    private static DockerClient CreateDockerClient()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        using var configuration = string.IsNullOrEmpty(dockerHost) ?
            new DockerClientConfiguration() :
            new DockerClientConfiguration(new Uri(dockerHost));
        return configuration.CreateClient();
    }

    private static Dictionary<string, IDictionary<string, bool>> CreateFilters(IDictionary<string, string> filters)
    {
        var result = new Dictionary<string, IDictionary<string, bool>>();
        foreach (var keyValuePair in filters)
        {
            var value = new Dictionary<string, bool>
            {
                { keyValuePair.Value, true }
            };
            result.Add(keyValuePair.Key, value);
        }

        return result;
    }

    private static string GetConfigurationPath()
    {
        var assembly = typeof(RabbitRunner).Assembly;
        var testDirectory = Path.GetDirectoryName(assembly.Location);
        return Path.Combine(testDirectory!, ConfigurationFileName);
    }

    private static async Task CreateConfigurationFile()
    {
        await File.WriteAllTextAsync(ConfigurationPath, Configuration);
    }

    private async Task PullImage()
    {
        // Do not pull image in parallel to avoid multiple downloads.
        await PullImageSemaphore.WaitAsync();
        try
        {
            var filters = new Dictionary<string, string>()
            {
                { "reference", Image }
            };
            var listParameters = new ImagesListParameters
            {
                Filters = CreateFilters(filters)
            };
            var images = await dockerClient.Value.Images.ListImagesAsync(listParameters);

            if (!images.SelectMany(i => i.RepoTags).Contains(Image, StringComparer.OrdinalIgnoreCase))
            {
                var createParameters = new ImagesCreateParameters
                {
                    FromImage = Image
                };
                await dockerClient.Value.Images.CreateImageAsync(createParameters, null, new Progress<JSONMessage>());
            }
        }
        finally
        {
            PullImageSemaphore.Release();
        }
    }

    private async Task<ContainerStatus> TryGetContainer()
    {
        var filters = new Dictionary<string, string>()
        {
            { "name", containerName },
            { "label", EnvironmentLabel }
        };
        var listParameters = new ContainersListParameters
        {
            All = true,
            Filters = CreateFilters(filters)
        };
        var containers = await dockerClient.Value.Containers.ListContainersAsync(listParameters);

        var containerResponse = containers.SingleOrDefault(IsMatch);
        if (containerResponse is not null)
        {
            var isRunning = string.Equals(containerResponse.State, "running", StringComparison.OrdinalIgnoreCase);
            var isConnected = containerResponse.NetworkSettings.Networks.Count > 0;
            return (containerResponse.ID, isRunning, isConnected);
        }
        else
        {
            return default;
        }

        bool IsMatch(ContainerListResponse container)
        {
            return container.Names.Any(n => n.TrimStart('/') == containerName) &&
                container.Labels.TryGetValue(EnvironmentLabel, out var label) &&
                label == EnvironmentId;
        }
    }

    [SuppressMessage("StyleCop.CSharp.LayoutRules", "SA1513:Closing brace should be followed by blank line", Justification = "There shouldn't be blank line before end of list.")]
    private async Task<ContainerStatus> CreateContainer()
    {
        await CreateConfigurationFile();

        var createContainerParameters = new CreateContainerParameters
        {
            Image = Image,
            Name = containerName,
            ExposedPorts = new Dictionary<string, EmptyStruct>()
            {
                { RabbitMQPort.ToString(CultureInfo.InvariantCulture) + "/tcp", default }
            },
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge",
                PortBindings = new Dictionary<string, IList<PortBinding>>()
                {
                    {
                        RabbitMQPort.ToString(CultureInfo.InvariantCulture) + "/tcp",
                        new List<PortBinding>()
                        {
                            new()
                            {
                                HostIP = string.Empty,
                                HostPort = Port.ToString(CultureInfo.InvariantCulture)
                            }
                        }
                    }
                },
                Mounts =
                [
                    new()
                    {
                        Type = "bind",
                        Source = ConfigurationPath,
                        Target = ContainerConfigurationPath,
                        ReadOnly = true
                    }
                ]
            },
            Env =
            [
                "RABBITMQ_DEFAULT_USER=" + Username,
                "RABBITMQ_DEFAULT_PASS=" + Password
            ],
            Labels = new Dictionary<string, string>()
            {
                { EnvironmentLabel, EnvironmentId },
                { PortLabel, Port.ToString(CultureInfo.InvariantCulture) }
            }
        };
        var containerResponse = await dockerClient.Value.Containers.CreateContainerAsync(createContainerParameters);
        return (containerResponse.ID, false, true);
    }

    private async Task<bool> WaitForInitialization(DateTimeOffset startTime)
    {
        const string searchText = "started TCP listener on [::]:5672";
        for (var timeout = DateTime.UtcNow.AddSeconds(30); DateTime.UtcNow <= timeout;)
        {
            var parameters = new ContainerLogsParameters
            {
                ShowStdout = true,
                Since = startTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            };
            using var outputStream = await dockerClient.Value.Containers.GetContainerLogsAsync(containerId, false, parameters);
            var (stdout, _) = await outputStream.ReadOutputToEndAsync(default);
            if (stdout.Contains(searchText, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
