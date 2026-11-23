using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Idempotency;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Infrastructure.Storage;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Nexus.IntegrationTests;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("nexus_test")
        .WithUsername("postgres")
        .WithPassword("postgres_test_pw")
        .Build();

    public string AdminConnectionString => _container.GetConnectionString();
    public string MigrationConnectionString { get; private set; } = string.Empty;
    public string RuntimeConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using (var adminConn = new NpgsqlConnection(AdminConnectionString))
        {
            await adminConn.OpenAsync();
            const string roleInitSql = """
                CREATE ROLE nexus_migrator LOGIN PASSWORD 'migrator_test_pw';
                CREATE ROLE nexus_runtime LOGIN PASSWORD 'runtime_test_pw';
                REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                GRANT USAGE, CREATE ON SCHEMA public TO nexus_migrator;
                GRANT USAGE ON SCHEMA public TO nexus_runtime;
                ALTER DEFAULT PRIVILEGES FOR ROLE nexus_migrator IN SCHEMA public
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nexus_runtime;
                ALTER DEFAULT PRIVILEGES FOR ROLE nexus_migrator IN SCHEMA public
                    GRANT USAGE, SELECT ON SEQUENCES TO nexus_runtime;
                """;
            await using var cmd = new NpgsqlCommand(roleInitSql, adminConn);
            await cmd.ExecuteNonQueryAsync();
        }

        var baseBuilder = new NpgsqlConnectionStringBuilder(AdminConnectionString);

        MigrationConnectionString = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString)
        {
            Username = "nexus_migrator",
            Password = "migrator_test_pw"
        }.ConnectionString;

        RuntimeConnectionString = new NpgsqlConnectionStringBuilder(baseBuilder.ConnectionString)
        {
            Username = "nexus_runtime",
            Password = "runtime_test_pw"
        }.ConnectionString;

        await DatabaseMigrator.MigrateAsync(MigrationConnectionString, requireTls: false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

public sealed class PostgresFoundationTests(PostgresContainerFixture pg)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public void DatabaseOptions_RejectsPort6543AndDisabledTlsWhenRequired()
    {
        var port6543Cs = new NpgsqlConnectionStringBuilder
        {
            Host = "aws-0.pooler.supabase.com",
            Port = 6543,
            Database = "postgres",
            Username = "nexus_runtime",
            SslMode = SslMode.Require
        }.ConnectionString;

        var ex6543 = Assert.Throws<InvalidOperationException>(() =>
            DatabaseOptions.ValidateConnectionString(port6543Cs, requireTls: true, "Runtime"));
        Assert.Contains("5432", ex6543.Message, StringComparison.Ordinal);

        var insecureTlsCs = new NpgsqlConnectionStringBuilder
        {
            Host = "db.example.internal",
            Port = 5432,
            Database = "nexus",
            Username = "nexus_runtime",
            SslMode = SslMode.Prefer
        }.ConnectionString;

        Assert.Throws<InvalidOperationException>(() =>
            DatabaseOptions.ValidateConnectionString(insecureTlsCs, requireTls: true, "Runtime"));

        var validTlsCs = new NpgsqlConnectionStringBuilder
        {
            Host = "aws-0.pooler.supabase.com",
            Port = 5432,
            Database = "postgres",
            Username = "nexus_runtime",
            SslMode = SslMode.Require
        }.ConnectionString;

        DatabaseOptions.ValidateConnectionString(validTlsCs, requireTls: true, "Runtime");
    }

    [Fact]
    public async Task SeparateRoles_RuntimeCanCrudApplicationTables_AndIsDeniedDdl()
    {
        var options = new DbContextOptionsBuilder<NexusDbContext>()
            .UseNpgsql(pg.RuntimeConnectionString)
            .Options;

        await using var db = new NexusDbContext(options);

        // Runtime role can INSERT / SELECT / DELETE on migrated tables
        var row = new IdempotencyKeyEntity
        {
            Id = Guid.CreateVersion7(),
            UserId = Guid.NewGuid(),
            HttpMethod = "POST",
            RouteTemplate = "/api/role-test",
            IdempotencyKey = "role-crud-" + Guid.NewGuid().ToString("N"),
            RequestHash = new string('a', 64),
            StatusCode = 200,
            ResponseJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        db.IdempotencyKeys.Add(row);
        await db.SaveChangesAsync();

        var fetched = await db.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        Assert.NotNull(fetched);

        db.IdempotencyKeys.Remove(row);
        await db.SaveChangesAsync();

        // Runtime role MUST be denied DDL (CREATE TABLE)
        await using var runtimeConn = new NpgsqlConnection(pg.RuntimeConnectionString);
        await runtimeConn.OpenAsync();
        await using var ddlCmd = new NpgsqlCommand("CREATE TABLE unauthorized_ddl_probe (id int);", runtimeConn);
        var pgEx = await Assert.ThrowsAsync<PostgresException>(() => ddlCmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, pgEx.SqlState);
    }

    [Fact]
    public async Task ConditionalTransition_EnforcesStateMachineSemanticsInPostgreSql()
    {
        var options = new DbContextOptionsBuilder<NexusDbContext>()
            .UseNpgsql(pg.RuntimeConnectionString)
            .Options;

        await using var db = new NexusDbContext(options);
        var sessionId = "cond-" + Guid.NewGuid().ToString("N");
        db.AppSessions.Add(new AppSessionEntity
        {
            Id = sessionId,
            UserId = Guid.NewGuid(),
            TicketBytes = [1, 2, 3],
            CreatedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var revokeTime = DateTimeOffset.UtcNow;

        // 1. Active -> Revoked succeeds
        var first = await ConditionalTransition.ExecuteAsync(
            ct => db.AppSessions
                .Where(s => s.Id == sessionId && s.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, revokeTime), ct),
            async ct =>
            {
                var current = await db.AppSessions.AsNoTracking()
                    .Where(s => s.Id == sessionId)
                    .Select(s => new { IsRevoked = s.RevokedAt != null })
                    .SingleOrDefaultAsync(ct);
                return current is null ? (false, false) : (true, current.IsRevoked);
            },
            targetState: true);
        Assert.Equal(TransitionOutcome.Succeeded, first.Outcome);
        Assert.True(first.IsSuccess);

        // 2. Replaying Active -> Revoked returns AlreadyInTargetState (idempotent)
        var replay = await ConditionalTransition.ExecuteAsync(
            ct => db.AppSessions
                .Where(s => s.Id == sessionId && s.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, revokeTime), ct),
            async ct =>
            {
                var current = await db.AppSessions.AsNoTracking()
                    .Where(s => s.Id == sessionId)
                    .Select(s => new { IsRevoked = s.RevokedAt != null })
                    .SingleOrDefaultAsync(ct);
                return current is null ? (false, false) : (true, current.IsRevoked);
            },
            targetState: true);
        Assert.Equal(TransitionOutcome.AlreadyInTargetState, replay.Outcome);
        Assert.True(replay.IsSuccess);

        // 3. Trying to transition Revoked -> Active when condition requires a different state yields Conflict
        var conflict = await ConditionalTransition.ExecuteAsync(
            ct => db.AppSessions
                .Where(s => s.Id == sessionId && s.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.LastActivityAt, DateTimeOffset.UtcNow), ct),
            async ct =>
            {
                var current = await db.AppSessions.AsNoTracking()
                    .Where(s => s.Id == sessionId)
                    .Select(s => new { State = s.RevokedAt == null ? "Active" : "Revoked" })
                    .SingleOrDefaultAsync(ct);
                return current is null ? (false, (string?)null) : (true, current.State);
            },
            targetState: "Pending");
        Assert.Equal(TransitionOutcome.Conflict, conflict.Outcome);
        Assert.False(conflict.IsSuccess);
        Assert.Equal("Revoked", conflict.CurrentState);

        // 4. Missing entity -> NotFound
        var missing = await ConditionalTransition.ExecuteAsync(
            _ => Task.FromResult(0),
            _ => Task.FromResult<(bool, string?)>((false, null)),
            targetState: "Revoked");
        Assert.Equal(TransitionOutcome.NotFound, missing.Outcome);
    }

    [Fact]
    public async Task M0FullAcceptance_Idempotency_PostgresSessions_DataProtection_SignalR_AndPrivatePdf()
    {
        var userId = Guid.NewGuid();
        var statusLookup = new InMemoryAccountStatusLookup();
        statusLookup.SetStatus(userId, AccountStatus.Active);

        var tempStorageDir = Path.Combine(Path.GetTempPath(), "nexus-m0-accept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorageDir);

        var handlerExecutions = 0;

        try
        {
            await using var app = NexusHost.Build(
                [
                    "--urls", "http://127.0.0.1:0",
                    "--NexusDatabase:RequireTls=false",
                    $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}",
                    "--NexusStorage:Provider=Local",
                    $"--NexusStorage:LocalRootPath={tempStorageDir}"
                ],
                configureBuilder: b =>
                {
                    b.Services.AddSingleton<IAccountStatusLookup>(statusLookup);
                },
                configureApp: a =>
                {
                    a.MapHub<AcceptanceHub>("/hubs/acceptance");

                    // Dev-only simulated login endpoint issuing a real BFF cookie persisted in PostgreSQL
                    a.MapPost("/api/m0/dev-login", async (HttpContext ctx) =>
                    {
                        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
                        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SecurityDefaultsExtensions.SessionAuthScheme));
                        await ctx.SignInAsync(
                            SecurityDefaultsExtensions.SessionAuthScheme,
                            principal,
                            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
                        return Results.Ok(new { userId });
                    }).AllowAnonymous().DisableAntiforgery();

                    // Idempotent write endpoint that also pushes a SignalR notification
                    a.MapPost("/api/m0/records", async (
                        HttpContext ctx,
                        IHubContext<AcceptanceHub> hub,
                        RecordInput input) =>
                    {
                        var count = Interlocked.Increment(ref handlerExecutions);
                        var id = Guid.CreateVersion7();
                        await hub.Clients.All.SendAsync("RecordCreated", new { id, input.Title, count });
                        return Results.Created($"/api/m0/records/{id}", new { id, input.Title, count });
                    })
                    .DisableAntiforgery()
                    .AddEndpointFilter<IdempotencyEndpointFilter>();

                    // Private PDF upload & authorized download endpoints (M0.9 + M0 acceptance)
                    a.MapPost("/api/m0/files", async (
                        HttpRequest req,
                        IFileStorage storage,
                        IFileScanner scanner) =>
                    {
                        await using var ms = new MemoryStream();
                        await req.Body.CopyToAsync(ms);
                        ms.Position = 0;
                        var scan = await scanner.ScanAsync(ms, "acceptance.pdf");
                        ms.Position = 0;
                        var stored = await storage.SaveAsync(ms, "application/pdf");
                        return Results.Created("/api/m0/files", new { stored.ObjectKey, stored.Sha256Hex, scanStatus = scan.StatusCode });
                    }).DisableAntiforgery();

                    a.MapGet("/api/m0/files/{*objectKey}", async (
                        string objectKey,
                        HttpContext ctx,
                        IFileStorage storage) =>
                    {
                        var stream = await storage.OpenReadAsync(objectKey);
                        if (stream is null)
                        {
                            return Results.NotFound();
                        }

                        StorageKeyHelper.ApplySafeDownloadHeaders(ctx.Response, "acceptance.pdf", "application/pdf");
                        return Results.Stream(stream, "application/pdf");
                    });
                });

            await app.StartAsync();
            var baseUri = new Uri(GetBaseAddress(app));

            var cookieContainer = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                CheckCertificateRevocationList = true
            };
            using var authedClient = new HttpClient(handler) { BaseAddress = baseUri };
            using var anonClient = new HttpClient { BaseAddress = baseUri };

            // 1. Readiness probe checks real PostgreSQL connectivity
            using var readyResp = await anonClient.GetAsync("/readyz");
            Assert.Equal(HttpStatusCode.OK, readyResp.StatusCode);
            var readyBody = await readyResp.Content.ReadAsStringAsync();
            Assert.Contains("\"postgres\"", readyBody);

            // 2. Unauthenticated call rejected with 401
            using var unauthResp = await anonClient.PostAsJsonAsync("/api/m0/records", new RecordInput("nope"));
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

            // 3. Simulated login persists session ticket and Data Protection keys in PostgreSQL (M0.8)
            using var loginResp = await authedClient.PostAsync("/api/m0/dev-login", null);
            Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
            Assert.NotEmpty(cookieContainer.GetCookies(baseUri));

            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                Assert.True(await db.DataProtectionKeys.AnyAsync(), "Expected Data Protection keys persisted in PostgreSQL.");
                Assert.True(await db.AppSessions.AnyAsync(s => s.UserId == userId), "Expected BFF session persisted in PostgreSQL.");
            }

            // 4. Connect SignalR with the BFF session cookie & receive live event
            var receivedSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var hubConn = new HubConnectionBuilder()
                .WithUrl(new Uri(baseUri, "hubs/acceptance"), opts =>
                {
                    opts.Cookies = cookieContainer;
                    opts.Headers.Add("Origin", baseUri.ToString().TrimEnd('/'));
                })
                .Build();
            hubConn.On<JsonElement>("RecordCreated", payload => receivedSignal.TrySetResult(payload.GetRawText()));
            await hubConn.StartAsync();

            // 5. Idempotent POST writes record & triggers SignalR event (M0.5)
            var idemKey = "m0-key-" + Guid.NewGuid().ToString("N");
            using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/m0/records")
            {
                Content = new StringContent("{\"title\":\"First Record\"}", Encoding.UTF8, "application/json")
            };
            req1.Headers.Add(IdempotencyEndpointFilter.HeaderName, idemKey);
            using var resp1 = await authedClient.SendAsync(req1);
            Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
            var body1 = await resp1.Content.ReadAsStringAsync();

            var signalPayload = await receivedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("First Record", signalPayload);
            Assert.Equal(1, handlerExecutions);

            // 6. Exact retry with equivalent JSON whitespace replays cached response without re-running handler
            using var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/m0/records")
            {
                Content = new StringContent("{  \"title\" : \"First Record\" }", Encoding.UTF8, "application/json")
            };
            req2.Headers.Add(IdempotencyEndpointFilter.HeaderName, idemKey);
            using var resp2 = await authedClient.SendAsync(req2);
            Assert.Equal(HttpStatusCode.Created, resp2.StatusCode);
            Assert.Equal("true", Assert.Single(resp2.Headers.GetValues(IdempotencyEndpointFilter.ReplayedHeaderName)));
            Assert.Equal(body1, await resp2.Content.ReadAsStringAsync());
            Assert.Equal(1, handlerExecutions);

            // 7. Conflicting payload under same Idempotency-Key returns 409 Conflict (SEC-19)
            using var reqConflict = new HttpRequestMessage(HttpMethod.Post, "/api/m0/records")
            {
                Content = new StringContent("{\"title\":\"Tampered Record\"}", Encoding.UTF8, "application/json")
            };
            reqConflict.Headers.Add(IdempotencyEndpointFilter.HeaderName, idemKey);
            using var respConflict = await authedClient.SendAsync(reqConflict);
            Assert.Equal(HttpStatusCode.Conflict, respConflict.StatusCode);

            // 8. Expired key allows fresh execution
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
                await db.IdempotencyKeys
                    .Where(x => x.IdempotencyKey == idemKey)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
            }
            using var reqAfterExpiry = new HttpRequestMessage(HttpMethod.Post, "/api/m0/records")
            {
                Content = new StringContent("{\"title\":\"After Expiry\"}", Encoding.UTF8, "application/json")
            };
            reqAfterExpiry.Headers.Add(IdempotencyEndpointFilter.HeaderName, idemKey);
            using var respAfterExpiry = await authedClient.SendAsync(reqAfterExpiry);
            Assert.Equal(HttpStatusCode.Created, respAfterExpiry.StatusCode);
            Assert.Equal(2, handlerExecutions);

            // 9. Private PDF upload & authorized download (M0.9 + M0 acceptance)
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n%Nexus M0 Private PDF\n%%EOF");
            using var uploadResp = await authedClient.PostAsync("/api/m0/files", new ByteArrayContent(pdfBytes));
            Assert.Equal(HttpStatusCode.Created, uploadResp.StatusCode);
            var uploadDoc = JsonDocument.Parse(await uploadResp.Content.ReadAsStringAsync());
            var objectKey = uploadDoc.RootElement.GetProperty("objectKey").GetString()!;
            Assert.Equal("skipped_prototype", uploadDoc.RootElement.GetProperty("scanStatus").GetString());

            // Authorized download succeeds with attachment + nosniff
            using var downloadResp = await authedClient.GetAsync($"/api/m0/files/{objectKey}");
            Assert.Equal(HttpStatusCode.OK, downloadResp.StatusCode);
            Assert.Equal("attachment; filename=\"acceptance.pdf\"", downloadResp.Content.Headers.ContentDisposition?.ToString());
            Assert.Equal("nosniff", Assert.Single(downloadResp.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal(pdfBytes, await downloadResp.Content.ReadAsByteArrayAsync());

            // Anonymous download denied (401)
            using var anonDownload = await anonClient.GetAsync($"/api/m0/files/{objectKey}");
            Assert.Equal(HttpStatusCode.Unauthorized, anonDownload.StatusCode);

            // Disabled account with existing BFF session cookie denied (403)
            statusLookup.SetStatus(userId, AccountStatus.Disabled);
            using var disabledDownload = await authedClient.GetAsync($"/api/m0/files/{objectKey}");
            Assert.Equal(HttpStatusCode.Forbidden, disabledDownload.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempStorageDir))
            {
                Directory.Delete(tempStorageDir, recursive: true);
            }
        }
    }

    private static string GetBaseAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        return address.EndsWith('/') ? address : address + "/";
    }

    private sealed record RecordInput(string Title);

    private sealed class AcceptanceHub : Hub
    {
        public string Ping() => Context.ConnectionId is not null ? "pong" : string.Empty;
    }
}
