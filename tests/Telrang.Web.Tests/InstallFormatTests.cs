using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Telrang.Web.Data;
using Telrang.Web.Hosting;
using Telrang.Web.Security;

namespace Telrang.Web.Tests;

/// <summary>
/// DESIGN.md §29: the configuration sources (command line, environment, operator file, shipped appsettings), the operator
/// file's refusals and permission warning, platform defaults for the file and the data directory, the data directory
/// check, the Windows service's admin key file and the healthcheck command.
/// </summary>
public class InstallFormatTests
{
    private const string Secret = "Pw-29-operator-file-never-shown";

    private static readonly HostPlatform Linux = new(false, false, false, false, null, null);
    private static readonly HostPlatform LinuxService = Linux with { IsSystemdService = true };
    private static readonly HostPlatform Container = Linux with { InContainer = true };
    private static readonly HostPlatform Windows = new(true, false, false, false, @"C:\ProgramData", null);
    private static readonly HostPlatform WindowsService = Windows with { IsWindowsService = true };

    private static string TempFile(string content)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("telrang-29-").FullName, "telrang.json");
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static void DeleteWithFolder(string file) => Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);

    // ---- sources and precedence --------------------------------------------------------------------

    [Fact]
    public void Command_line_beats_environment_beats_operator_file_beats_shipped_appsettings()
    {
        var s = "Prec" + Guid.NewGuid().ToString("N");
        var root = Directory.CreateTempSubdirectory("telrang-29-root-");
        var vars = new[] { $"{s}__Env", $"{s}__Cli", $"ASPNETCORE_{s}__Aspnet" };
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "appsettings.json"),
                $$"""{ "{{s}}": { "Shipped": "shipped", "File": "shipped", "Env": "shipped", "Cli": "shipped", "Aspnet": "shipped" } }""");
            Environment.SetEnvironmentVariable(vars[0], "env");
            Environment.SetEnvironmentVariable(vars[1], "env");
            Environment.SetEnvironmentVariable(vars[2], "aspnet");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [$"--{s}:Cli=cli"],
                ContentRootPath = root.FullName,
                EnvironmentName = "Production",
            });
            OperatorConfig.Insert(builder.Configuration, OperatorConfig.Parse(System.Text.Encoding.UTF8.GetBytes(
                $$"""{ "{{s}}": { "File": "file", "Env": "file", "Cli": "file", "Aspnet": "file" } }"""), "test.json"));

            var config = builder.Configuration;
            Assert.Equal("shipped", config[$"{s}:Shipped"]);
            Assert.Equal("file", config[$"{s}:File"]);
            Assert.Equal("env", config[$"{s}:Env"]);
            Assert.Equal("cli", config[$"{s}:Cli"]);
            // ASPNETCORE_URLS and friends are environment too: they beat the file.
            Assert.Equal("aspnet", config[$"{s}:Aspnet"]);
        }
        finally
        {
            foreach (var v in vars) Environment.SetEnvironmentVariable(v, null);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_operator_file_takes_comments_trailing_commas_and_nested_sections()
    {
        var values = OperatorConfig.Parse(System.Text.Encoding.UTF8.GetBytes("""
            {
              // PostgreSQL on another host
              "Database": { "Provider": "Postgres", },
              "ConnectionStrings": { "Default": "Host=db;Password=x" },
              "Kestrel": { "Certificates": { "Default": { "Path": "/etc/telrang/cert.pfx" } } },
              "Urls": "http://127.0.0.1:8080",
            }
            """), "telrang.json");
        Assert.Equal("Postgres", values["Database:Provider"]);
        Assert.Equal("Host=db;Password=x", values["ConnectionStrings:Default"]);
        Assert.Equal("/etc/telrang/cert.pfx", values["Kestrel:Certificates:Default:Path"]);
        Assert.Equal("http://127.0.0.1:8080", values["urls"]);
    }

    [Theory]
    [InlineData("{ \"ConnectionStrings\": { \"Default\": \"" + Secret + "\" ", "not valid JSON at line 1")]
    [InlineData("{ \"Database\": { \"Provider\": " + Secret + " } }", "not valid JSON at line 1")]
    [InlineData("[ \"" + Secret + "\" ]", "must hold one JSON object")]
    [InlineData("\"" + Secret + "\"", "must hold one JSON object")]
    [InlineData("{ \"A\": \"" + Secret + "\", \"a\": \"" + Secret + "\" }", "a key appears twice")]
    public void A_malformed_file_is_refused_without_quoting_it(string content, string expected)
    {
        var e = Assert.Throws<OperatorConfigException>(() => OperatorConfig.Parse(System.Text.Encoding.UTF8.GetBytes(content), "/etc/telrang/telrang.json"));
        Assert.Contains(expected, e.Message);
        Assert.Contains("/etc/telrang/telrang.json", e.Message);
        Assert.DoesNotContain(Secret, e.Message);
        Assert.DoesNotContain("Pw-", e.Message);
    }

    [Fact]
    public void A_utf16_file_is_refused_with_a_hint()
    {
        var bytes = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("{}")).ToArray();
        var e = Assert.Throws<OperatorConfigException>(() => OperatorConfig.Parse(bytes, "telrang.json"));
        Assert.Contains("save it as UTF-8", e.Message);
        // A UTF-8 byte order mark is fine (Notepad).
        Assert.Empty(OperatorConfig.Parse(System.Text.Encoding.UTF8.GetPreamble().Concat("{}"u8.ToArray()).ToArray(), "telrang.json"));
    }

    [Fact]
    public void A_missing_default_file_is_fine_but_a_missing_named_file_is_not()
    {
        var missing = Path.Combine(Path.GetTempPath(), "telrang-29-missing-" + Guid.NewGuid().ToString("N"), "telrang.json");
        Assert.Null(OperatorConfig.Read(missing, named: false));
        var e = Assert.Throws<OperatorConfigException>(() => OperatorConfig.Read(missing, named: true));
        Assert.Contains("TELRANG_CONFIG", e.Message);
        Assert.Contains("does not exist", e.Message);

        var dir = Directory.CreateTempSubdirectory("telrang-29-dir-");
        try
        {
            Assert.Contains("is a directory", Assert.Throws<OperatorConfigException>(() => OperatorConfig.Read(dir.FullName, named: false)).Message);
        }
        finally
        {
            dir.Delete();
        }
    }

    [Fact]
    public void A_default_file_in_a_folder_this_account_cannot_read_is_refused_not_skipped()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // root reads everything
        var folder = Directory.CreateTempSubdirectory("telrang-29-closed-");
        try
        {
            File.SetUnixFileMode(folder.FullName, UnixFileMode.None);
            var e = Assert.Throws<OperatorConfigException>(() => OperatorConfig.Read(Path.Combine(folder.FullName, "telrang.json"), named: false));
            Assert.Contains("cannot be read by this account", e.Message);
        }
        finally
        {
            File.SetUnixFileMode(folder.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void TELRANG_CONFIG_names_the_file_or_turns_it_off_and_the_default_follows_the_platform()
    {
        Assert.Equal((HostPlatform.LinuxConfigFile, false), OperatorConfig.Resolve(null, Linux));
        Assert.Equal((HostPlatform.LinuxConfigFile, false), OperatorConfig.Resolve("  ", LinuxService));
        Assert.Null(OperatorConfig.Resolve(null, Container)); // Docker: environment only
        Assert.Null(OperatorConfig.Resolve("none", Linux));
        Assert.Null(OperatorConfig.Resolve("NONE", Windows));
        Assert.Equal(("/srv/telrang.json", true), OperatorConfig.Resolve("/srv/telrang.json", Linux));
        Assert.Equal(("/srv/telrang.json", true), OperatorConfig.Resolve("/srv/telrang.json", Container));
        Assert.Equal(Path.Combine(@"C:\ProgramData", "Telrang", "telrang.json"), Windows.DefaultConfigFile);
        Assert.Equal(WindowsService.DefaultConfigFile, Windows.DefaultConfigFile); // a shell command reads the service's file
    }

    [Fact]
    public void The_default_data_directory_follows_the_platform_and_service_mode()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "telrang-29-content"));
        Assert.Equal(Path.Combine(root, "data"), Linux.DefaultDataDir(root));
        Assert.Equal(Path.Combine(root, "data"), Container.DefaultDataDir(root));
        Assert.Equal(Path.Combine(root, "data"), Windows.DefaultDataDir(root));
        Assert.Equal("/var/lib/telrang", LinuxService.DefaultDataDir(root));
        Assert.Equal("/var/lib/telrang", (LinuxService with { StateDirectory = "" }).DefaultDataDir(root));
        Assert.Equal("/home/u/.local/state/telrang", (LinuxService with { StateDirectory = "/home/u/.local/state/telrang" }).DefaultDataDir(root));
        Assert.Equal("/var/lib/telrang", (LinuxService with { StateDirectory = "/var/lib/telrang:/var/lib/other" }).DefaultDataDir(root));
        Assert.Equal(Path.Combine(@"C:\ProgramData", "Telrang", "data"), WindowsService.DefaultDataDir(root));
        // systemd as PID 1 inside a container is not the Linux install format.
        Assert.Equal(Path.Combine(root, "data"), (Container with { IsSystemdService = true }).DefaultDataDir(root));
        Assert.True(LinuxService.IsService);
        Assert.True(WindowsService.IsService);
        Assert.False(Linux.IsService);
        Assert.False((Container with { IsSystemdService = true }).IsService);
    }

    // ---- permissions -------------------------------------------------------------------------------

    [Fact]
    public void A_config_file_open_to_others_gets_a_warning()
    {
        if (OperatingSystem.IsWindows()) return; // the ACL check needs a Windows machine
        var file = TempFile("{}");
        try
        {
            const UnixFileMode rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Null(FilePermissions.ConfigFileProblem(file));
            File.SetUnixFileMode(file, rw | UnixFileMode.GroupRead); // root:telrang 0640, the documented setup
            Assert.Null(FilePermissions.ConfigFileProblem(file));
            File.SetUnixFileMode(file, rw | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var warning = FilePermissions.ConfigFileProblem(file);
            Assert.NotNull(warning);
            Assert.Contains("mode 0644", warning);
            Assert.Contains("chmod 640", warning);
            File.SetUnixFileMode(file, rw | UnixFileMode.GroupWrite);
            Assert.Contains("mode 0620", FilePermissions.ConfigFileProblem(file));
        }
        finally
        {
            DeleteWithFolder(file);
        }
    }

    [Fact]
    public void A_data_directory_or_key_ring_open_to_others_gets_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Directory.CreateTempSubdirectory("telrang-29-data-").FullName, "data");
        try
        {
            DataDirectory.Prepare(dir);
            const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(owner, File.GetUnixFileMode(dir));
            Assert.Equal(owner, File.GetUnixFileMode(DataDirectory.KeysPath(dir)));
            Assert.Null(FilePermissions.DataDirProblem(dir));

            File.SetUnixFileMode(DataDirectory.KeysPath(dir), owner | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            Assert.Contains("keys is open to other users (mode 0750)", FilePermissions.DataDirProblem(dir));
            File.SetUnixFileMode(DataDirectory.KeysPath(dir), owner);
            File.SetUnixFileMode(dir, owner | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Assert.Contains("chmod 700 " + dir, FilePermissions.DataDirProblem(dir));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [Fact]
    public async Task The_key_ring_is_in_the_data_directory_and_unencrypted_only_off_Windows()
    {
        await using var factory = new AppFactory();
        var options = factory.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var repository = Assert.IsType<Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository>(options.XmlRepository);
        Assert.Equal(DataDirectory.KeysPath(factory.DataDir), repository.Directory.FullName);
        if (OperatingSystem.IsWindows())
            Assert.IsType<Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlEncryptor>(options.XmlEncryptor);
        else
            Assert.Null(options.XmlEncryptor);
    }

    // ---- the Windows service's admin key file ------------------------------------------------------

    [Fact]
    public void With_a_key_file_the_setup_key_goes_to_the_file_and_the_log_only_points_to_it()
    {
        var dir = Directory.CreateTempSubdirectory("telrang-29-key-");
        try
        {
            var service = Linux with { IsWindowsService = true };
            File.WriteAllText(Path.Combine(dir.FullName, ServiceHosting.AdminKeyFileName), "stale");
            var file = ServiceHosting.PrepareAdminKeyFile(service, dir.FullName);
            Assert.NotNull(file);
            Assert.False(File.Exists(file)); // an earlier start's key is gone

            var log = new ListLogger();
            AdminBootstrapper.LogIssued(new AdminBootstrapper.Issued("KEY-29-abc", null), log, "https://vote.example.org", [], file);
            Assert.Contains("Admin setup key: KEY-29-abc (valid 1 h). Open https://vote.example.org/setup and enter it.", File.ReadAllText(file!));
            var line = Assert.Single(log.Lines);
            Assert.DoesNotContain("KEY-29-abc", line);
            Assert.Contains(file!, line);

            // Not a Windows service: no file, the key goes to the console log as before.
            File.WriteAllText(file!, "stale");
            Assert.Null(ServiceHosting.PrepareAdminKeyFile(Linux, dir.FullName));
            Assert.False(File.Exists(file));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public readonly List<string> Lines = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    // ---- healthcheck -------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "http://127.0.0.1:8080")]
    [InlineData("http://+:8080", "http://127.0.0.1:8080")]
    [InlineData("https://+:8443;http://*:8080", "http://127.0.0.1:8080")]
    [InlineData("https://0.0.0.0:8443", "https://127.0.0.1:8443")]
    [InlineData("http://[::]:5000", "http://127.0.0.1:5000")]
    [InlineData("http://localhost:5099", "http://localhost:5099")]
    public void Healthcheck_picks_a_local_http_address(string? urls, string expected) =>
        Assert.Equal(expected, HealthCheckCommand.DefaultUrl(urls));

    [Fact]
    public async Task Healthcheck_asks_loopback_only()
    {
        var output = new StringWriter();
        Assert.Equal(2, await HealthCheckCommand.RunAsync(["--url", "http://example.org/"], output));
        Assert.Contains("must be loopback", output.ToString());
        Assert.Equal(2, await HealthCheckCommand.RunAsync(["--url", "file:///etc/passwd"], output));
        Assert.Equal(1, await HealthCheckCommand.RunAsync(["--url", $"http://127.0.0.1:{AppProcess.FreePort()}"], output));
    }

    // ---- as a process ------------------------------------------------------------------------------

    [Fact]
    public async Task A_malformed_or_missing_named_file_ends_the_process_with_exit_code_2()
    {
        var file = TempFile("{ \"ConnectionStrings\": { \"Default\": \"Password=" + Secret + "\" }, }}");
        try
        {
            var (code, output) = await AppProcess.RunToExitAsync(new Dictionary<string, string> { ["TELRANG_CONFIG"] = file });
            Assert.Equal(StartupExit.Configuration, code);
            Assert.Contains($"Telrang cannot start: The configuration file {file} is not valid JSON at line 1.", output);
            Assert.DoesNotContain(Secret, output);

            (code, output) = await AppProcess.RunToExitAsync(new Dictionary<string, string> { ["TELRANG_CONFIG"] = file + ".missing" });
            Assert.Equal(StartupExit.Configuration, code);
            Assert.Contains("does not exist", output);
        }
        finally
        {
            DeleteWithFolder(file);
        }
    }

    [Fact]
    public async Task The_file_configures_the_database_the_environment_overrides_it_and_its_secret_stays_out_of_the_output()
    {
        var file = TempFile($$"""
            {
              "Database": { "Provider": "Postgres" },
              "ConnectionStrings": { "Default": "Host=127.0.0.1;Port=1;Database=telrang_29_db;Username=telrang_29_user;Password={{Secret}};Timeout=3" }
            }
            """);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var (code, output) = await AppProcess.RunToExitAsync(new Dictionary<string, string> { ["TELRANG_CONFIG"] = file });
            Assert.Equal(StartupExit.Database, code); // the file's PostgreSQL, unreachable
            Assert.Contains("Configuration file: " + file, output);
            if (!OperatingSystem.IsWindows()) Assert.Contains($"The configuration file {file} is readable or writable by other users (mode 0644)", output);
            Assert.DoesNotContain(Secret, output);
            Assert.DoesNotContain("telrang_29_user", output);
            Assert.DoesNotContain("telrang_29_db", output);

            // The environment beats the file, the command line beats the environment.
            (code, output) = await AppProcess.RunToExitAsync(new Dictionary<string, string> { ["TELRANG_CONFIG"] = file, ["Database__Provider"] = "Oracle" });
            Assert.Equal(StartupExit.Configuration, code);
            (code, _) = await AppProcess.RunToExitAsync(new Dictionary<string, string> { ["TELRANG_CONFIG"] = file, ["Database__Provider"] = "Sqlite" },
                "--Database:Provider=Oracle");
            Assert.Equal(StartupExit.Configuration, code);
            Assert.DoesNotContain(Secret, output);
        }
        finally
        {
            DeleteWithFolder(file);
        }
    }

    [Fact]
    public async Task Urls_from_the_file_are_served_and_healthcheck_reaches_them()
    {
        var port = AppProcess.FreePort();
        var file = TempFile($$"""{ "Urls": "http://127.0.0.1:{{port}}" }""");
        try
        {
            var env = new Dictionary<string, string> { ["TELRANG_CONFIG"] = file, ["ASPNETCORE_URLS"] = AppProcess.Unset };
            var output = await AppProcess.RunWhileHealthyAsync(env, $"http://127.0.0.1:{port}", async () =>
            {
                var check = new StringWriter();
                Assert.Equal(0, await HealthCheckCommand.RunAsync(["--url", $"http://127.0.0.1:{port}"], check));
                Assert.Contains("healthcheck: 200", check.ToString());
            });
            Assert.Contains("Configuration file: " + file, output);
            Assert.DoesNotContain("is open to other users", output); // a data directory the app created itself
        }
        finally
        {
            DeleteWithFolder(file);
        }
    }

    [Fact]
    public void Nothing_from_the_operator_file_reaches_the_database_setup_message()
    {
        var values = OperatorConfig.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{ "Database": { "Provider": "Postgres" }, "ConnectionStrings": { "Default": "Host=db;Password={{Secret}};Bogus Keyword=1" } }"""), "f");
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var e = Assert.Throws<DatabaseConfigException>(() => DatabaseSetup.FromConfiguration(config, "/data"));
        Assert.DoesNotContain(Secret, e.Message);
    }
}

/// <summary>The files shipped in the Linux and Windows archives (packaging/, DESIGN.md §29).</summary>
public class PackagingTests
{
    private static string Repo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Telrang.slnx"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }

    // Linux: the unit's Unix socket; Windows: loopback with the proxy there trusted explicitly (pentest R3-DEP-3, R3-DEP-7).
    [Theory]
    [InlineData("linux", "/var/lib/telrang", "http://unix:/run/telrang/telrang.sock", null)]
    [InlineData("windows", @"C:\ProgramData\Telrang\data", "http://127.0.0.1:8080", "127.0.0.1")]
    public void The_example_configuration_parses_and_names_the_service_data_directory(string platform, string dataDir, string urls, string? proxies)
    {
        var values = OperatorConfig.Parse(File.ReadAllBytes(Repo("packaging", platform, "telrang.json")), "telrang.json");
        Assert.Equal(dataDir, values["App:DataDir"]);
        Assert.Equal(urls, values["Urls"]);
        Assert.Equal(proxies, values.GetValueOrDefault("ReverseProxy:KnownProxies"));
        Assert.False(values.ContainsKey("ConnectionStrings:Default")); // no password in an example
    }

    [Fact]
    public void The_unit_runs_sandboxed_as_its_own_user_with_a_private_state_directory()
    {
        var unit = File.ReadAllLines(Repo("packaging", "linux", "telrang.service")).Select(l => l.Trim()).ToHashSet();
        foreach (var line in new[]
                 {
                     "Type=notify", "User=telrang", "ExecStart=/opt/telrang/Telrang.Web", "StateDirectory=telrang",
                     "StateDirectoryMode=0700", "ConfigurationDirectory=telrang", "UMask=0077", "RestartPreventExitStatus=2",
                     "NoNewPrivileges=yes", "CapabilityBoundingSet=", "ProtectSystem=strict", "ProtectHome=yes", "PrivateTmp=yes",
                     "SystemCallFilter=@system-service", "Environment=DOTNET_EnableDiagnostics=0",
                     "RuntimeDirectory=telrang", "RuntimeDirectoryMode=0750", "Group=telrang-proxy", "SupplementaryGroups=telrang",
                 })
        {
            Assert.Contains(line, unit);
        }
        Assert.DoesNotContain(unit, l => l.StartsWith("MemoryDenyWriteExecute=yes", StringComparison.Ordinal)); // the JIT needs W+X
    }

    [Fact]
    public void The_windows_scripts_are_ascii_for_windows_powershell()
    {
        foreach (var file in new[] { "install-service.ps1", "uninstall-service.ps1", "telrang.json" })
        {
            var bytes = File.ReadAllBytes(Repo("packaging", "windows", file));
            Assert.All(bytes, b => Assert.True(b < 0x80, $"{file} has a non-ASCII byte (PowerShell 5.1 reads it as ANSI)"));
        }
    }
}
