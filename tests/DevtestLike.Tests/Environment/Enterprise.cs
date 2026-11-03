using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;
using DevtestLike.Tests.Environment;

namespace DevtestLike.Tests;

/// <summary>
/// The interconnected systems under test, started once for the whole run: a real Postgres and a real Keycloak
/// (in Docker, through Testcontainers), the virtual rating engine (WireMock.Net), and the policy service itself,
/// hosted in-process and wired to all three. DevTest calls this the test environment; here it's code.
/// It sits in the tests' root namespace because NUnit runs a SetUpFixture only for tests in or below its own.
/// </summary>
[SetUpFixture]
public sealed class Enterprise
{
    public const string Realm = "devtestlike";
    public const string AgentUser = "agent-007";
    public const string AgentPassword = "correct-horse-battery";

    private static PostgreSqlContainer postgres = null!;
    private static KeycloakContainer keycloak = null!;

    public static RatingEngine RatingEngine { get; private set; } = null!;
    public static WebApplicationFactory<Program> PolicyService { get; private set; } = null!;
    public static NpgsqlDataSource Database { get; private set; } = null!;
    public static string TokenEndpoint => $"{keycloak.GetBaseAddress().TrimEnd('/')}/realms/{Realm}/protocol/openid-connect/token";

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
        keycloak = new KeycloakBuilder()
            .WithImage("quay.io/keycloak/keycloak:26.0")
            .WithResourceMapping(new FileInfo(Path.Combine(AppContext.BaseDirectory, "Environment", "devtestlike-realm.json")), "/opt/keycloak/data/import/")
            .WithCommand("--import-realm")
            .Build();
        await Task.WhenAll(postgres.StartAsync(), keycloak.StartAsync());

        RatingEngine = RatingEngine.Start();
        Database = NpgsqlDataSource.Create(postgres.GetConnectionString());
        PolicyService = Host(new FakeTimeProvider(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The policy service, wired to the environment. Tests that need to move the clock (expiring quotes) host
    /// their own copy with their own <paramref name="clock"/>.
    /// </summary>
    public static WebApplicationFactory<Program> Host(TimeProvider clock, double ratingTimeoutSeconds = 2) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("ConnectionStrings:Policies", postgres.GetConnectionString());
            web.UseSetting("RatingEngine:Url", RatingEngine.Url);
            web.UseSetting("RatingEngine:TimeoutSeconds", ratingTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            web.UseSetting("Auth:Authority", $"{keycloak.GetBaseAddress().TrimEnd('/')}/realms/{Realm}");
            web.UseSetting("Auth:RequireHttps", "false");
            web.UseSetting("Auth:AllowedClients", "policy-client");
            web.UseSetting("Legacy:Username", AgentUser);
            web.UseSetting("Legacy:Password", AgentPassword);
            web.ConfigureServices(services => services.AddSingleton(clock));
        });

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        await PolicyService.DisposeAsync();
        RatingEngine.Dispose();
        await Database.DisposeAsync();
        await postgres.DisposeAsync();
        await keycloak.DisposeAsync();
    }
}
