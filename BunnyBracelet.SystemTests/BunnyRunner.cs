﻿using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BunnyBracelet.SystemTests;

/// <summary>
/// This object controls an instance of BunnyBracelet program
/// that is object of the system tests.
/// </summary>
/// <remarks>
/// Additionally it provides console output of the BunnyBracelet program.
/// </remarks>
internal sealed class BunnyRunner : IAsyncDisposable
{
#if DEBUG
    private const string Configuration = "debug";
#else
    private const string Configuration = "release";
#endif

    private const string DefaultInboundExchangePrefix = "test-inbound-";
    private const string DefaultOutboundExchangePrefix = "test-outbound-";

    private static readonly Lazy<string> LazyBunnyBraceletPath = new(GetBunnyBraceletPath);

    private readonly StringBuilder output = new();
    private readonly Lock outputLock = new();
    private Process? process;

    private BunnyRunner(
        int port,
        string rabbitMQUri,
        ExchangeSettings inboundExchange,
        ExchangeSettings outboundExchange,
        IReadOnlyList<EndpointSettings> endpoints)
    {
        Port = port;
        RabbitMQUri = rabbitMQUri;
        InboundExchange = inboundExchange;
        OutboundExchange = outboundExchange;
        Endpoints = endpoints;
    }

    public int Port { get; }

    public string Uri => GetUri(Port);

    public string RabbitMQUri { get; }

    public ExchangeSettings InboundExchange { get; }

    public ExchangeSettings OutboundExchange { get; }

    public IReadOnlyList<EndpointSettings> Endpoints { get; }

    public int UseAuthenticationKeyIndex { get; set; }

    public string? AuthenticationKey1 { get; set; }

    public string? AuthenticationKey2 { get; set; }

    public int? Timeout { get; set; }

    public int? RequeueDelay { get; set; }

    public int? ExitCode { get; private set; }

    internal static string BunnyBraceletPath => LazyBunnyBraceletPath.Value;

    public static string GetUri(int port) => "http://localhost:" + port.ToString(CultureInfo.InvariantCulture);

    public static BunnyRunner Create(
        int port,
        string rabbitMQUri,
        string? inboundExchange = null,
        string? outboundExchange = null,
        int? endpointPort = default)
    {
        var endpoint = endpointPort.HasValue ? GetUri(endpointPort.Value) : null;
        return Create(port, rabbitMQUri, inboundExchange, outboundExchange, endpoint);
    }

    public static BunnyRunner Create(
        int port,
        string rabbitMQUri,
        string? inboundExchange = null,
        string? outboundExchange = null,
        string? endpoint = null)
    {
        var inboundExchangeSettings = new ExchangeSettings
        {
            Name = inboundExchange ?? DefaultInboundExchangePrefix + Guid.NewGuid().ToString()
        };
        var outboundExchangeSettings = new ExchangeSettings
        {
            Name = outboundExchange ?? DefaultOutboundExchangePrefix + Guid.NewGuid().ToString()
        };
        var endpoints = new List<EndpointSettings>();
        if (endpoint is not null)
        {
            endpoints.Add(new EndpointSettings { Uri = endpoint });
        }

        return new BunnyRunner(port, rabbitMQUri, inboundExchangeSettings, outboundExchangeSettings, endpoints);
    }

    public static BunnyRunner Create(
        int port,
        string rabbitMQUri,
        string? inboundExchange = null,
        string? outboundExchange = null,
        IReadOnlyList<int>? endpointPorts = null)
    {
        IReadOnlyList<string> endpoints = [];
        if (endpointPorts is not null)
        {
            endpoints = endpointPorts.Select(GetUri).ToList();
        }

        return Create(port, rabbitMQUri, inboundExchange, outboundExchange, endpoints);
    }

    public static BunnyRunner Create(
        int port,
        string rabbitMQUri,
        string? inboundExchange = null,
        string? outboundExchange = null,
        IReadOnlyList<string>? endpoints = null)
    {
        var inboundExchangeSettings = new ExchangeSettings
        {
            Name = inboundExchange ?? DefaultInboundExchangePrefix + Guid.NewGuid().ToString()
        };
        var outboundExchangeSettings = new ExchangeSettings
        {
            Name = outboundExchange ?? DefaultOutboundExchangePrefix + Guid.NewGuid().ToString()
        };
        IReadOnlyList<EndpointSettings> endpointSettings = [];
        if (endpoints is not null)
        {
            endpointSettings = endpoints.Select(e => new EndpointSettings { Uri = e }).ToList();
        }

        return new BunnyRunner(port, rabbitMQUri, inboundExchangeSettings, outboundExchangeSettings, endpointSettings);
    }

    public static BunnyRunner Create(
        int port,
        string rabbitMQUri,
        ExchangeSettings? inboundExchange = null,
        ExchangeSettings? outboundExchange = null,
        IReadOnlyList<EndpointSettings>? endpoints = null)
    {
        inboundExchange ??= new ExchangeSettings
        {
            Name = DefaultInboundExchangePrefix + Guid.NewGuid().ToString()
        };

        outboundExchange ??= new ExchangeSettings
        {
            Name = DefaultOutboundExchangePrefix + Guid.NewGuid().ToString()
        };

        endpoints ??= [];

        return new BunnyRunner(port, rabbitMQUri, inboundExchange, outboundExchange, endpoints);
    }

    public async Task Start()
    {
        if (process is null)
        {
            process = Process.Start(CreateProcessStartInfo());
            Debug.Assert(process != null, "BunnyBracelet process was not started.");

            process.Exited += ProcessOnExited;
            process.OutputDataReceived += ProcessOnOutputDataReceived;
            process.ErrorDataReceived += ProcessOnErrorDataReceived;
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!await WaitForInitialization())
            {
                throw new InvalidOperationException("BunneBracelet initialization failed. Output:" + Environment.NewLine + GetOutput());
            }
        }
    }

    public async Task Stop()
    {
        if (process is not null)
        {
            if (!process.HasExited)
            {
                if (OperatingSystem.IsWindows())
                {
                    process.Kill();
                }
                else
                {
                    if (SysKill(process.Id, 2) == 0)
                    {
                        using var cancelationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await process.WaitForExitAsync(cancelationTokenSource.Token);
                    }

                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
            }

            process.Dispose();
            process = null;
        }
    }

    public string GetOutput()
    {
        lock (outputLock)
        {
            return output.ToString();
        }
    }

    [SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Other HTTP status codes are unexpected.")]
    public async Task<HealthStatus> GetHealthStatus()
    {
        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(Uri);
        var response = await httpClient.GetAsync(new Uri("/health", UriKind.Relative));

        return response.StatusCode switch
        {
            HttpStatusCode.OK => HealthStatus.Healthy,
            HttpStatusCode.ServiceUnavailable => HealthStatus.Unhealthy,
            _ => throw new InvalidOperationException($"Unexpected health-check HTTP status code {response.StatusCode}.")
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Stop();
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SysKill(int pid, int sig);

    private static string GetBunnyBraceletPath()
    {
        var testAssemblyPath = typeof(SystemTest).Assembly.Location;
        testAssemblyPath = Path.GetDirectoryName(testAssemblyPath);
        var path = Path.Combine(testAssemblyPath!, "..", "..", "..", "publish", "BunnyBracelet");
        path = Path.GetFullPath(path);

        var filename = "BunnyBracelet";
        var pivotBase = Configuration + "_net9.0";
        var pivot = pivotBase + "_linux-x64";
        if (OperatingSystem.IsWindows())
        {
            filename += ".exe";
            pivot = pivotBase + "_win-x64";
        }

        // Run AOT published version, if it is found.
        var result = Path.Combine(path, pivot, filename);
        if (File.Exists(result))
        {
            return result;
        }

        return Path.Combine(path, pivotBase, filename);
    }

    private static void SetEndpointEnvironment(IDictionary<string, string?> environment, EndpointSettings endpointSettings, int index)
    {
        var prefix = $"BunnyBracelet__Endpoints__{index.ToString(CultureInfo.InvariantCulture)}__";
        var queuePrefix = prefix + "Queue__";

        if (endpointSettings.Uri is not null)
        {
            environment[prefix + "Uri"] = endpointSettings.Uri;
        }

        if (endpointSettings.QueueName is not null)
        {
            environment[queuePrefix + "Name"] = endpointSettings.QueueName;
        }

        if (endpointSettings.Durable.HasValue)
        {
            environment[queuePrefix + "Durable"] = endpointSettings.Durable.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (endpointSettings.AutoDelete.HasValue)
        {
            environment[queuePrefix + "AutoDelete"] = endpointSettings.AutoDelete.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void ProcessOnExited(object? sender, EventArgs e)
    {
        ExitCode = process!.ExitCode;
    }

    private void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        lock (outputLock)
        {
            output.AppendLine(e.Data);
        }
    }

    private void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        lock (outputLock)
        {
            output.AppendLine(e.Data);
        }
    }

    private ProcessStartInfo CreateProcessStartInfo()
    {
        var result = new ProcessStartInfo(BunnyBraceletPath)
        {
            WorkingDirectory = Path.GetDirectoryName(BunnyBraceletPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        result.Environment["ASPNETCORE_URLS"] = "http://localhost:" + Port.ToString(CultureInfo.InvariantCulture);
        result.Environment["BunnyBracelet__RabbitMQUri"] = RabbitMQUri;
        SetInboundExchangeEnvironment(result.Environment);
        SetOutboundExchangeEnvironment(result.Environment);

        for (var i = 0; i < Endpoints.Count; i++)
        {
            SetEndpointEnvironment(result.Environment, Endpoints[i], i);
        }

        if (UseAuthenticationKeyIndex != 0)
        {
            result.Environment["BunnyBracelet__Authentication__UseKeyIndex"] = UseAuthenticationKeyIndex.ToString(CultureInfo.InvariantCulture);
        }

        if (AuthenticationKey1 is not null)
        {
            result.Environment["BunnyBracelet__Authentication__Key1"] = AuthenticationKey1;
        }

        if (AuthenticationKey2 is not null)
        {
            result.Environment["BunnyBracelet__Authentication__Key2"] = AuthenticationKey2;
        }

        if (Timeout.HasValue)
        {
            result.Environment["BunnyBracelet__Timeout"] = Timeout.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (RequeueDelay.HasValue)
        {
            result.Environment["BunnyBracelet__RequeueDelay"] = RequeueDelay.Value.ToString(CultureInfo.InvariantCulture);
        }

        return result;
    }

    private void SetInboundExchangeEnvironment(IDictionary<string, string?> environment)
    {
        const string prefix = "BunnyBracelet__InboundExchange__";

        if (InboundExchange.Name is not null)
        {
            environment[prefix + "Name"] = InboundExchange.Name;
        }

        if (InboundExchange.Type is not null)
        {
            environment[prefix + "Type"] = InboundExchange.Type;
        }

        if (InboundExchange.Durable.HasValue)
        {
            environment[prefix + "Durable"] = InboundExchange.Durable.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (InboundExchange.AutoDelete.HasValue)
        {
            environment[prefix + "AutoDelete"] = InboundExchange.AutoDelete.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void SetOutboundExchangeEnvironment(IDictionary<string, string?> environment)
    {
        const string prefix = "BunnyBracelet__OutboundExchange__";

        if (OutboundExchange.Name is not null)
        {
            environment[prefix + "Name"] = OutboundExchange.Name;
        }

        if (OutboundExchange.Type is not null)
        {
            environment[prefix + "Type"] = OutboundExchange.Type;
        }

        if (OutboundExchange.Durable.HasValue)
        {
            environment[prefix + "Durable"] = OutboundExchange.Durable.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (OutboundExchange.AutoDelete.HasValue)
        {
            environment[prefix + "AutoDelete"] = OutboundExchange.AutoDelete.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private async Task<bool> WaitForInitialization()
    {
        var searchText = "Now listening on: http://localhost:" + Port.ToString(CultureInfo.InvariantCulture);
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow <= timeout && process is not null && !process.HasExited)
        {
            var processOutput = GetOutput();
            if (processOutput.Contains(searchText, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
