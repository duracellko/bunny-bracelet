using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BunnyBracelet.SystemTests;

internal sealed class BunnyRunner : IAsyncDisposable
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private static readonly Lazy<string> LazyBunnyBraceletPath = new Lazy<string>(GetBunnyBraceletPath);

    private readonly StringBuilder output = new StringBuilder();
    private readonly object outputLock = new object();
    private Process? process;

    public BunnyRunner(
        int port,
        string rabbitMQUri,
        string inboundExchange,
        string outboundExchange,
        params string[] endpoints)
    {
        Port = port;
        RabbitMQUri = rabbitMQUri;
        InboundExchange = inboundExchange;
        OutboundExchange = outboundExchange;
        Endpoints = endpoints ?? Array.Empty<string>();
    }

    public int Port { get; }

    public string RabbitMQUri { get; }

    public string InboundExchange { get; }

    public string OutboundExchange { get; }

    public IReadOnlyList<string> Endpoints { get; }

    public int? ExitCode { get; private set; }

    internal static string BunnyBraceletPath => LazyBunnyBraceletPath.Value;

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
        var path = Path.Combine(testAssemblyPath!, "..", "..", "..", "..", "BunnyBracelet", "bin", Configuration, "net8.0");
        path = Path.GetFullPath(path);

        var filename = "BunnyBracelet";
        var runtimeId = "linux-x64";
        if (OperatingSystem.IsWindows())
        {
            filename += ".exe";
            runtimeId = "win-x64";
        }

        // Run AOT published version, if it is found.
        var nativePath = Path.Combine(path, runtimeId, "native");
        var result = Path.Combine(nativePath, filename);
        if (File.Exists(result))
        {
            return result;
        }

        return Path.Combine(path, filename);
    }

    private void ProcessOnExited(object? sender, EventArgs e)
    {
        ExitCode = process!.ExitCode;
    }

    private void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        lock (outputLock)
        {
            output.Append(e.Data);
        }
    }

    private void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        lock (outputLock)
        {
            output.Append(e.Data);
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
        result.Environment["BunnyBracelet__InboundExchange"] = InboundExchange;
        result.Environment["BunnyBracelet__OutboundExchange"] = OutboundExchange;

        for (int i = 0; i < Endpoints.Count; i++)
        {
            result.Environment["BunnyBracelet__Endpoints__" + i.ToString(CultureInfo.InvariantCulture)] = Endpoints[i];
        }

        return result;
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
