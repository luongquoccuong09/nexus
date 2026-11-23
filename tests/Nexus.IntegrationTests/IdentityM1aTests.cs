using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.IntegrationTests;

public sealed class IdentityM1aTests(PostgresContainerFixture pg)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task SEC20_OpenRegistration_CreatesUnverifiedAccount_EnforcesUniqueReservedHoneypotAndRateLimit()
    {
        await using var app = BuildHost(
            "--NexusIdentity:RegistrationMode=Open",
            "--NexusIdentity:RegisterRateLimitPerMinutePerIp=6");
        await app.StartAsync();

        var (_, client) = CreateCookieClient(app);

        // 1. Valid open registration creates Unverified Member and returns noindex header
        var u1 = "trial_" + Guid.NewGuid().ToString("N")[..8];
        using var reg1 = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = u1,
            password = "Correct-Horse-Battery-99!",
            displayName = "Trial Member",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Created, reg1.StatusCode);
        Assert.Equal("noindex, nofollow", Assert.Single(reg1.Headers.GetValues("X-Robots-Tag")));

        var reg1Doc = JsonDocument.Parse(await reg1.Content.ReadAsStringAsync());
        Assert.Equal("Unverified", reg1Doc.RootElement.GetProperty("trustLevel").GetString());
        Assert.True(reg1Doc.RootElement.GetProperty("isUnverified").GetBoolean());

        // 2. Duplicate username -> 409 Conflict
        using var dupResp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = u1,
            password = "Another-Strong-Password-88!",
            displayName = "Duplicate User",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Conflict, dupResp.StatusCode);

        // 3. Reserved username -> 400 BadRequest
        using var reservedResp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "administrator",
            password = "Another-Strong-Password-88!",
            displayName = "Fake Admin",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, reservedResp.StatusCode);

        // 4. Honeypot populated -> 400 BadRequest
        using var hpResp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "bot_" + Guid.NewGuid().ToString("N")[..8],
            password = "Another-Strong-Password-88!",
            displayName = "Bot User",
            termsAccepted = true,
            honeypot = "https://spam.example"
        });
        Assert.Equal(HttpStatusCode.BadRequest, hpResp.StatusCode);

        // 5. Exceed per-IP registration rate limit -> 429 Too Many Requests (SEC-20)
        HttpStatusCode? lastCode = null;
        for (var i = 0; i < 6; i++)
        {
            using var burst = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = $"burst_{i}_{Guid.NewGuid().ToString("N")[..6]}",
                password = "Burst-Strong-Password-77!",
                displayName = $"Burst {i}",
                termsAccepted = true
            });
            lastCode = burst.StatusCode;
            if (lastCode == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, lastCode);
    }

    [Fact]
    public async Task RegistrationModes_EnforceJoinCodeAndInviteOnly()
    {
        // OpenWithJoinCode
        await using (var app = BuildHost(
            "--NexusIdentity:RegistrationMode=OpenWithJoinCode",
            "--NexusIdentity:JoinCode=NEXUS-2026"))
        {
            await app.StartAsync();
            var (_, client) = CreateCookieClient(app);

            using var wrongCode = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = "jc_" + Guid.NewGuid().ToString("N")[..8],
                password = "Valid-Long-Password-99!",
                displayName = "Join Code User",
                termsAccepted = true,
                joinCode = "WRONG"
            });
            Assert.Equal(HttpStatusCode.Forbidden, wrongCode.StatusCode);

            using var rightCode = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = "jc_" + Guid.NewGuid().ToString("N")[..8],
                password = "Valid-Long-Password-99!",
                displayName = "Join Code User",
                termsAccepted = true,
                joinCode = "NEXUS-2026"
            });
            Assert.Equal(HttpStatusCode.Created, rightCode.StatusCode);
        }

        // InviteOnly blocks open registration
        await using (var app = BuildHost("--NexusIdentity:RegistrationMode=InviteOnly"))
        {
            await app.StartAsync();
            var (_, client) = CreateCookieClient(app);

            using var blocked = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = "inv_" + Guid.NewGuid().ToString("N")[..8],
                password = "Valid-Long-Password-99!",
                displayName = "Blocked User",
                termsAccepted = true
            });
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        }
    }

    [Fact]
    public async Task SEC01_SEC21_PasswordPolicy_GenericLoginErrors_AndAccountLockout()
    {
        await using var app = BuildHost(
            "--NexusIdentity:MaxFailedLoginAttempts=3",
            "--NexusIdentity:LoginRateLimitPerMinutePerIp=30");
        await app.StartAsync();

        var (_, client) = CreateCookieClient(app);
        var username = "sec01_" + Guid.NewGuid().ToString("N")[..8];
        const string validPassword = "Unique-Strong-Passphrase-2026!";

        // SEC-21: short password rejected
        using var shortPw = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = "Short123!",
            displayName = "Sec User",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, shortPw.StatusCode);

        // SEC-21: common password rejected
        using var commonPw = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = "password123456",
            displayName = "Sec User",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, commonPw.StatusCode);

        // Valid registration
        using var okReg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = validPassword,
            displayName = "Sec User",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Created, okReg.StatusCode);

        // SEC-01: wrong password and unknown username return identical generic 401 payload
        using var badPassResp = await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Wrong-Password-999!" });
        using var missingUserResp = await client.PostAsJsonAsync("/api/auth/login", new { username = "nonexistent_user_xyz", password = "Wrong-Password-999!" });

        Assert.Equal(HttpStatusCode.Unauthorized, badPassResp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missingUserResp.StatusCode);
        Assert.Equal(await missingUserResp.Content.ReadAsStringAsync(), await badPassResp.Content.ReadAsStringAsync());

        // Trigger lockout (2 more failures -> 3 total)
        await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Wrong-Password-999!" });
        await client.PostAsJsonAsync("/api/auth/login", new { username, password = "Wrong-Password-999!" });

        // Even the correct password is now rejected with generic 401 while locked out
        using var lockedResp = await client.PostAsJsonAsync("/api/auth/login", new { username, password = validPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, lockedResp.StatusCode);
    }

    [Fact]
    public async Task SEC24_M1_5_M1_7_ChangePassword_RemoteSignOut_AndAdminResetLink()
    {
        await using var app = BuildHost(
            configureApp: a => a.MapHub<IdentityProbeHub>("/hubs/id-probe"));
        await app.StartAsync();

        var baseUri = new Uri(GetBaseAddress(app));
        var username = "sec24_" + Guid.NewGuid().ToString("N")[..8];
        const string initialPw = "Initial-Strong-Password-2026!";
        const string rotatedPw = "Rotated-Strong-Password-2026!";

        var (_, devA) = CreateCookieClient(app);
        var (cookiesB, devB) = CreateCookieClient(app);
        var (_, devC) = CreateCookieClient(app);

        using var reg = await devA.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = initialPw,
            displayName = "Multi Device User",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        // Sign in on Device B and Device C
        using var loginB = await devB.PostAsJsonAsync("/api/auth/login", new { username, password = initialPw });
        Assert.Equal(HttpStatusCode.OK, loginB.StatusCode);
        using var loginC = await devC.PostAsJsonAsync("/api/auth/login", new { username, password = initialPw });
        Assert.Equal(HttpStatusCode.OK, loginC.StatusCode);

        // M1.7: list signed-in devices (3 sessions) and remotely sign out Device C
        using var sessionsResp = await devA.GetAsync("/api/auth/sessions");
        Assert.Equal(HttpStatusCode.OK, sessionsResp.StatusCode);
        var sessionsDoc = JsonDocument.Parse(await sessionsResp.Content.ReadAsStringAsync());
        var items = sessionsDoc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        var otherSessionId = items.First(s => !s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetString()!;
        using var delResp = await devA.DeleteAsync($"/api/auth/sessions/{otherSessionId}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // Connect live SignalR socket on Device B (if B was the revoked one, re-login B first)
        using var checkB = await devB.GetAsync("/api/auth/me");
        if (checkB.StatusCode != HttpStatusCode.OK)
        {
            using var reLoginB = await devB.PostAsJsonAsync("/api/auth/login", new { username, password = initialPw });
            Assert.Equal(HttpStatusCode.OK, reLoginB.StatusCode);
        }

        await using var hubB = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "hubs/id-probe"), opts =>
            {
                opts.Cookies = cookiesB;
                opts.Headers.Add("Origin", baseUri.ToString().TrimEnd('/'));
            })
            .Build();
        await hubB.StartAsync();
        Assert.Equal("pong", await hubB.InvokeAsync<string>("Ping"));

        // M1.5 / SEC-24: Device A changes password -> rotates security stamp, revokes Device B, aborts SignalR
        using var changeResp = await devA.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = initialPw,
            newPassword = rotatedPw
        });
        Assert.Equal(HttpStatusCode.OK, changeResp.StatusCode);

        // Device A stays authenticated
        using var meA = await devA.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meA.StatusCode);

        // Device B is revoked immediately
        using var meB = await devB.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meB.StatusCode);

        // Device B's SignalR socket was aborted
        await Assert.ThrowsAnyAsync<Exception>(() => hubB.InvokeAsync<string>("Ping"));
    }

    [Fact]
    public async Task SEC08_SEC16_SEC20b_M1_8_AdminMfa_VerifyEmployee_Rename_ResetLink_AndDisable()
    {
        await using var app = BuildHost(
            configureApp: a => a.MapHub<IdentityProbeHub>("/hubs/id-probe"));
        await app.StartAsync();

        var baseUri = new Uri(GetBaseAddress(app));
        var adminUser = "sysadm_" + Guid.NewGuid().ToString("N")[..6];
        const string adminPw = "SystemAdmin-Strong-Pass-2026!";

        // M1.1: Bootstrap initial Verified SystemAdmin + seed HR employee record
        var bootstrappedAdmin = await IdentityBootstrapService.BootstrapSystemAdminAsync(
            app.Services, adminUser, adminPw, "System Admin");

        var empCode = "EMP-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            db.Employees.Add(new EmployeeEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = bootstrappedAdmin.OrganizationId!.Value,
                EmployeeCode = empCode,
                FullName = "Nguyen Van A",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Register an Unverified trial user
        var trialUser = "trial_" + Guid.NewGuid().ToString("N")[..6];
        const string trialPw = "Trial-User-Strong-Pass-2026!";
        var (trialCookies, trialClient) = CreateCookieClient(app);
        using var trialReg = await trialClient.PostAsJsonAsync("/api/auth/register", new
        {
            username = trialUser,
            password = trialPw,
            displayName = "Impersonator Name",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Created, trialReg.StatusCode);
        var trialId = JsonDocument.Parse(await trialReg.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        await OrganizationFixtureHandler.AdmitAsync(app.Services, trialId);

        // SEC-20b: Unverified member denied admin account endpoints
        using var unverifiedAdminCall = await trialClient.GetAsync("/api/admin/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, unverifiedAdminCall.StatusCode);

        // M1.8: SystemAdmin logs in without MFA -> blocked from /api/admin/* until MFA is enabled
        var (_, adminClient) = CreateCookieClient(app);
        using var adminLogin1 = await adminClient.PostAsJsonAsync("/api/auth/login", new { username = adminUser, password = adminPw });
        Assert.Equal(HttpStatusCode.OK, adminLogin1.StatusCode);
        var adminLoginDoc = JsonDocument.Parse(await adminLogin1.Content.ReadAsStringAsync());
        Assert.True(adminLoginDoc.RootElement.GetProperty("mfaSetupRequired").GetBoolean());

        using var preMfaAdminCall = await adminClient.GetAsync("/api/admin/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, preMfaAdminCall.StatusCode);

        // Enroll TOTP MFA using RFC 6238 code
        using var mfaSetupResp = await adminClient.PostAsync("/api/auth/mfa/setup", null);
        Assert.Equal(HttpStatusCode.OK, mfaSetupResp.StatusCode);
        var sharedKey = JsonDocument.Parse(await mfaSetupResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sharedKey").GetString()!;

        var totpCode = GenerateTotpCode(sharedKey);
        using var mfaEnableResp = await adminClient.PostAsJsonAsync("/api/auth/mfa/enable", new { code = totpCode });
        Assert.Equal(HttpStatusCode.OK, mfaEnableResp.StatusCode);

        // Sign out and verify MFA challenge is enforced on next admin login
        await adminClient.PostAsync("/api/auth/logout", null);
        using var missingTotpLogin = await adminClient.PostAsJsonAsync("/api/auth/login", new { username = adminUser, password = adminPw });
        Assert.Equal(HttpStatusCode.Unauthorized, missingTotpLogin.StatusCode);
        Assert.True(JsonDocument.Parse(await missingTotpLogin.Content.ReadAsStringAsync()).RootElement.GetProperty("requiresMfa").GetBoolean());

        using var mfaLogin = await adminClient.PostAsJsonAsync("/api/auth/login", new
        {
            username = adminUser,
            password = adminPw,
            totpCode = GenerateTotpCode(sharedKey)
        });
        Assert.Equal(HttpStatusCode.OK, mfaLogin.StatusCode);

        // M1.4b: Admin renames impersonating user & verifies against HR employee record
        using var renameResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{trialId}/rename", new { displayName = "Nguyen Van A" });
        Assert.Equal(HttpStatusCode.OK, renameResp.StatusCode);
        Assert.Equal("Nguyen Van A", JsonDocument.Parse(await renameResp.Content.ReadAsStringAsync()).RootElement.GetProperty("displayName").GetString());

        using var verifyResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{trialId}/verify", new { employeeCode = empCode });
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        Assert.Equal("Verified", JsonDocument.Parse(await verifyResp.Content.ReadAsStringAsync()).RootElement.GetProperty("trustLevel").GetString());

        // M1.5: Admin issues audited one-time reset link & user redeems it once
        using var resetLinkResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{trialId}/reset-link", new { });
        Assert.Equal(HttpStatusCode.NotFound, resetLinkResp.StatusCode);
        var resetToken = await IdentityBootstrapService.IssueAccountRecoveryAsync(app.Services, trialUser);

        const string resetPw = "Reset-By-Admin-Link-2026!";
        using var redeem1 = await trialClient.PostAsJsonAsync("/api/auth/reset-password", new { token = resetToken, newPassword = resetPw });
        Assert.Equal(HttpStatusCode.OK, redeem1.StatusCode);

        // Reusing the one-time token fails
        using var redeem2 = await trialClient.PostAsJsonAsync("/api/auth/reset-password", new { token = resetToken, newPassword = "Another-Reset-Pass-2026!" });
        Assert.Equal(HttpStatusCode.BadRequest, redeem2.StatusCode);

        // Target user logs back in with new password and opens a live SignalR connection
        using var trialRelogin = await trialClient.PostAsJsonAsync("/api/auth/login", new { username = trialUser, password = resetPw });
        Assert.Equal(HttpStatusCode.OK, trialRelogin.StatusCode);

        await using var trialHub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "hubs/id-probe"), opts =>
            {
                opts.Cookies = trialCookies;
                opts.Headers.Add("Origin", baseUri.ToString().TrimEnd('/'));
            })
            .Build();
        await trialHub.StartAsync();
        Assert.Equal("pong", await trialHub.InvokeAsync<string>("Ping"));

        // M1.6 / SEC-08 / SEC-16: Admin disables target user -> HTTP revoked + SignalR aborted + audited
        using var disableResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{trialId}/status", new { status = "Disabled" });
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);

        using var disabledMe = await trialClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, disabledMe.StatusCode);
        using var disabledOrg = await trialClient.GetAsync("/api/people/directory");
        Assert.Equal(HttpStatusCode.Forbidden, disabledOrg.StatusCode);
        Assert.Equal("pong", await trialHub.InvokeAsync<string>("Ping"));

        // Verify audit trail persisted in PostgreSQL
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var actions = await db.AuditLogs
                .Where(a => a.ResourceId == trialId.ToString())
                .Select(a => a.Action)
                .ToListAsync();

            Assert.Contains("ACCOUNT_RENAME", actions);
            Assert.Contains("ACCOUNT_VERIFY", actions);
            Assert.Contains("OPERATOR_ACCOUNT_RESET_ISSUED", actions);
            Assert.Contains("PASSWORD_RESET_REDEEMED", actions);
            Assert.Contains("ACCOUNT_STATUS_CHANGE", actions);
        }
    }

    private WebApplication BuildHost(params string[] extraArgs) =>
        BuildHost(configureApp: null, extraArgs: extraArgs);

    private WebApplication BuildHost(Action<WebApplication>? configureApp, params string[] extraArgs)
    {
        var args = new List<string>
        {
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        };
        args.AddRange(extraArgs);
        return NexusHost.Build([.. args], configureApp: configureApp);
    }

    private static (CookieContainer Cookies, HttpClient Client) CreateCookieClient(WebApplication app)
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            CheckCertificateRevocationList = true
        };
        var client = new HttpClient(new OrganizationFixtureHandler(app.Services, handler)) { BaseAddress = new Uri(GetBaseAddress(app)) };
        return (cookies, client);
    }

    private static string GetBaseAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        return address.EndsWith('/') ? address : address + "/";
    }

    internal static string GenerateTotpCode(string base32Key)
    {
        var keyBytes = Base32Decode(base32Key);
        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

#pragma warning disable CA5350 // RFC 6238 TOTP used by ASP.NET Core Identity requires HMAC-SHA1 by specification.
        using var hmac = new HMACSHA1(keyBytes);
#pragma warning restore CA5350
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);
        var otp = binary % 1_000_000;
        return otp.ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleaned = input.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var c in cleaned)
        {
            var idx = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (idx < 0) continue;
            buffer = (buffer << 5) | idx;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return [.. output];
    }

    private sealed class IdentityProbeHub : Hub
    {
        public string Ping() => Context.ConnectionId is not null ? "pong" : string.Empty;
    }
}
