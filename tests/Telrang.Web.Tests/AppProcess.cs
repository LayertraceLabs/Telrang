using System.Diagnostics;

namespace Telrang.Web.Tests;

/// <summary>
/// Runs the built app (<c>dotnet Telrang.Web.dll</c> from the test output) as a separate process, for what only a real
/// process shows: exit codes, console output, the configuration sources (DESIGN.md §28, §29). Production environment, a
/// fresh data directory, no operator file (<c>TELRANG_CONFIG=none</c>) and no database variables from the test runner's
/// environment unless <c>env</c> sets them.
/// </summary>
public static class AppProcess
{
    /// <summary>An <c>env</c> value that removes the variable instead of setting it.</summary>
    public const string Unset = "\0unset";

    public static ProcessStartInfo StartInfo(string dataDir, IReadOnlyDictionary<string, string> env, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Telrang.Web.dll"));
        foreach (var a in args) start.ArgumentList.Add(a);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.Ordinal)
                     || k.StartsWith("Database__", StringComparison.Ordinal) || k == "TELRANG_CONFIG").ToList())
            start.Environment.Remove(key);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["App__DataDir"] = dataDir;
        start.Environment["TELRANG_CONFIG"] = "none";
        foreach (var (k, v) in env)
        {
            if (v == Unset) start.Environment.Remove(k);
            else start.Environment[k] = v;
        }
        return start;
    }

    /// <summary>Runs until the process ends (at most 90 s); stdout and stderr together.</summary>
    public static async Task<(int ExitCode, string Output)> RunToExitAsync(IReadOnlyDictionary<string, string> env, params string[] args)
    {
        var dataDir = Directory.CreateTempSubdirectory("telrang-proc-");
        try
        {
            using var process = Process.Start(StartInfo(dataDir.FullName, env, args))!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            dataDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Starts the app, waits until <c>{baseUrl}/healthz</c> answers 200, runs <paramref name="whileRunning"/>, then stops
    /// the process. Returns the output so far. Fails when the process ends or does not answer within 60 s.
    /// </summary>
    public static async Task<string> RunWhileHealthyAsync(IReadOnlyDictionary<string, string> env, string baseUrl,
        Func<Task> whileRunning, string? dataDir = null)
    {
        var ownDir = dataDir is null ? Directory.CreateTempSubdirectory("telrang-proc-") : null;
        var output = new System.Text.StringBuilder();
        using var process = new Process { StartInfo = StartInfo(dataDir ?? ownDir!.FullName, env, []) };
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException($"app exited with {process.ExitCode}:\n{Text()}");
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"no /healthz answer at {baseUrl}:\n{Text()}");
                try
                {
                    if ((await http.GetAsync(baseUrl + "/healthz")).IsSuccessStatusCode) break;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
                await Task.Delay(200);
            }
            await whileRunning();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            ownDir?.Delete(recursive: true);
        }
        return Text();

        string Text()
        {
            lock (output) return output.ToString();
        }
    }

    /// <summary>A TCP port nobody listens on right now.</summary>
    public static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
