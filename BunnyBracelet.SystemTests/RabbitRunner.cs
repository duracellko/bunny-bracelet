using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace BunnyBracelet.SystemTests;

internal sealed class RabbitRunner : IAsyncDisposable
{
    private const string Image = "rabbitmq:3.13";
    private const int RabbitMQPort = 5672;

    private static readonly SemaphoreSlim PullImageSemaphore = new SemaphoreSlim(1, 1);

    private readonly Lazy<DockerClient> dockerClient = new Lazy<DockerClient>(CreateDockerClient);
    private readonly string containerName;
    private string? containerId;

    public RabbitRunner(int port = RabbitMQPort, string? username = null, string? password = null)
    {
        Port = port;
        Username = string.IsNullOrEmpty(username) ? "bunny" : username;
        Password = string.IsNullOrEmpty(password) ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) : password;
        containerName = "rabbitmq-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    public int Port { get; }

    public string Username { get; }

    public string Password { get; }

    public string Uri => $"amqp://{Username}:{Password}@localhost:{Port.ToString(CultureInfo.InvariantCulture)}/";

    public async Task Start()
    {
        if (containerId is null)
        {
            await PullImage();
            containerId = await CreateContainer();
            await dockerClient.Value.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            if (!await WaitForInitialization())
            {
                throw new InvalidOperationException("RabbitMQ initialization failed.");
            }
        }
    }

    public async Task Stop()
    {
        if (containerId is not null)
        {
            await dockerClient.Value.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
            await dockerClient.Value.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters());
        }
    }

    public RabbitConnection CreateConnection() => new RabbitConnection(Uri);

    public async ValueTask DisposeAsync()
    {
        await Stop();

        if (dockerClient.IsValueCreated)
        {
            dockerClient.Value.Dispose();
        }
    }

    private static DockerClient CreateDockerClient()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        var uri = string.IsNullOrEmpty(dockerHost) ? null : new Uri(dockerHost);
        using var configuration = new DockerClientConfiguration(uri);
        return configuration.CreateClient();
    }

    private async Task PullImage()
    {
        // Do not pull image in parallel to avoid multiple downloads.
        await PullImageSemaphore.WaitAsync();
        try
        {
            var listParameters = new ImagesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>()
            {
                {
                    "reference",
                    new Dictionary<string, bool>()
                    {
                        { Image, true }
                    }
                }
            }
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

    private async Task<string> CreateContainer()
    {
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
                            new PortBinding
                            {
                                HostIP = string.Empty,
                                HostPort = Port.ToString(CultureInfo.InvariantCulture)
                            }
                        }
                    }
                }
            },
            Env = new List<string>
            {
                "RABBITMQ_DEFAULT_USER=" + Username,
                "RABBITMQ_DEFAULT_PASS=" + Password
            }
        };
        var containerResponse = await dockerClient.Value.Containers.CreateContainerAsync(createContainerParameters);
        return containerResponse.ID;
    }

    private async Task<bool> WaitForInitialization()
    {
        const string searchText = "started TCP listener on [::]:5672";
        for (var timeout = DateTime.UtcNow.AddSeconds(30); DateTime.UtcNow <= timeout;)
        {
            var parameters = new ContainerLogsParameters
            {
                ShowStdout = true
            };
            using var outputStream = await dockerClient.Value.Containers.GetContainerLogsAsync(containerId, false, parameters);
            var containerOutput = await outputStream.ReadOutputToEndAsync(default);
            if (containerOutput.stdout.Contains(searchText, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
