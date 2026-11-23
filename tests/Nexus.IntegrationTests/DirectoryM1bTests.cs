using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexus.Web;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;
using Nexus.Web.Modules.People;
using SkiaSharp;

namespace Nexus.IntegrationTests;

public sealed class DirectoryM1bTests(PostgresContainerFixture pg)
    : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task M1_9_HrCsvImport_DryRun_IdempotentUpsert_DiffReport_MatchSuggestions_AndAuthz()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var hrUsername = $"hr_{suffix}";
        var sysUsername = $"sys_{suffix}";
        const string pw = "Admin-Directory-Strong-2026!";

        var hrUser = await SeedVerifiedUserAsync(app.Services, hrUsername, pw, "HR Lead", OrgRole.HRAdmin);
        await SeedVerifiedUserAsync(app.Services, sysUsername, pw, "Sys Admin", OrgRole.SystemAdmin);

        // Register an Unverified account with ASCII Vietnamese name matching an incoming HR row
        var (_, unverifiedClient) = CreateCookieClient(app);
        var trialUsername = $"quan_{suffix}";
        using var trialReg = await unverifiedClient.PostAsJsonAsync("/api/auth/register", new
        {
            username = trialUsername,
            password = "Trial-Strong-Password-2026!",
            displayName = "Dang Minh Quan",
            termsAccepted = true
        });
        Assert.Equal(HttpStatusCode.Created, trialReg.StatusCode);
        await OrganizationFixtureHandler.AdmitAsync(app.Services, (await trialReg.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        // Login HRAdmin and enroll MFA (required for admin actions)
        var hrClient = await LoginAndEnableMfaAsync(app, hrUsername, pw);
        var sysClient = await LoginAndEnableMfaAsync(app, sysUsername, pw);

        var code1 = $"EMP1{suffix}".ToUpperInvariant();
        var code2 = $"EMP2{suffix}".ToUpperInvariant();
        var code3 = $"EMP3{suffix}".ToUpperInvariant();
        var deptEng = $"ENG{suffix}".ToUpperInvariant();
        var deptHr = $"HR{suffix}".ToUpperInvariant();

        var csvV1 = $"""
            employee_code,full_name,email,department_code,department_name,branch_code,branch_name,is_active
            {code1},Đặng Minh Quân,quan@company.local,{deptEng},Engineering,HCM,Ho Chi Minh,true
            {code2},Nguyễn Thị Thảo,thao@company.local,{deptEng},Engineering,HCM,Ho Chi Minh,true
            {code3},Lê Hoàng Nam,nam@company.local,{deptHr},Human Resources,DN,Da Nang,true
            """;

        // 1. SystemAdmin & Unverified member are DENIED HR import (only HRAdmin allowed per P0.2)
        using var sysImport = await sysClient.PostAsJsonAsync("/api/org/employees/import?dryRun=true", new { csvContent = csvV1 });
        Assert.Equal(HttpStatusCode.Forbidden, sysImport.StatusCode);

        using var memberImport = await unverifiedClient.PostAsJsonAsync("/api/org/employees/import?dryRun=true", new { csvContent = csvV1 });
        Assert.Equal(HttpStatusCode.Forbidden, memberImport.StatusCode);

        // 2. Dry run computes full diff and Unverified match suggestion WITHOUT persisting to PostgreSQL
        using var dryResp = await hrClient.PostAsJsonAsync("/api/org/employees/import?dryRun=true", new { csvContent = csvV1 });
        Assert.Equal(HttpStatusCode.OK, dryResp.StatusCode);
        var dryDoc = JsonDocument.Parse(await dryResp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(dryDoc.GetProperty("dryRun").GetBoolean());
        Assert.Equal(3, dryDoc.GetProperty("addedCount").GetInt32());
        Assert.Equal(2, dryDoc.GetProperty("departmentsCreated").GetInt32());
        Assert.Contains(dryDoc.GetProperty("matchSuggestions").EnumerateArray(),
            s => s.GetProperty("username").GetString() == trialUsername &&
                 s.GetProperty("employeeCode").GetString() == code1 &&
                 s.GetProperty("confidence").GetString() == "high");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.False(await db.Employees.AnyAsync(e => e.EmployeeCode == code1), "Dry run must not persist employees.");
        }

        // 3. Apply import persists employees, departments, branches, and writes audit log
        using var applyResp = await hrClient.PostAsJsonAsync("/api/org/employees/import?dryRun=false", new { csvContent = csvV1 });
        Assert.Equal(HttpStatusCode.OK, applyResp.StatusCode);
        var applyDoc = JsonDocument.Parse(await applyResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, applyDoc.GetProperty("addedCount").GetInt32());

        // 4. Re-importing identical CSV is idempotent (0 added, 0 updated, 3 unchanged)
        using var repeatResp = await hrClient.PostAsJsonAsync("/api/org/employees/import?dryRun=false", new { csvContent = csvV1 });
        Assert.Equal(HttpStatusCode.OK, repeatResp.StatusCode);
        var repeatDoc = JsonDocument.Parse(await repeatResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, repeatDoc.GetProperty("addedCount").GetInt32());
        Assert.Equal(0, repeatDoc.GetProperty("updatedCount").GetInt32());
        Assert.Equal(3, repeatDoc.GetProperty("unchangedCount").GetInt32());

        // 5. Updating 1 record and deactivating 1 record reports accurate diff
        var csvV2 = $"""
            employee_code,full_name,email,department_code,department_name,branch_code,branch_name,is_active
            {code1},Đặng Minh Quân,quan@company.local,{deptEng},Engineering,HCM,Ho Chi Minh,true
            {code2},Nguyễn Thị Thảo Nhi,thao@company.local,{deptEng},Engineering,HCM,Ho Chi Minh,true
            {code3},Lê Hoàng Nam,nam@company.local,{deptHr},Human Resources,DN,Da Nang,false
            """;
        using var diffResp = await hrClient.PostAsJsonAsync("/api/org/employees/import?dryRun=false", new { csvContent = csvV2 });
        Assert.Equal(HttpStatusCode.OK, diffResp.StatusCode);
        var diffDoc = JsonDocument.Parse(await diffResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, diffDoc.GetProperty("updatedCount").GetInt32());
        Assert.Equal(1, diffDoc.GetProperty("deactivatedCount").GetInt32());
        Assert.Equal(1, diffDoc.GetProperty("unchangedCount").GetInt32());

        // 6. Match suggestions endpoint surfaces Unverified -> Employee match and audit log exists
        using var suggResp = await hrClient.GetAsync("/api/org/employees/match-suggestions");
        Assert.Equal(HttpStatusCode.OK, suggResp.StatusCode);
        var suggText = await suggResp.Content.ReadAsStringAsync();
        Assert.Contains(trialUsername, suggText);
        Assert.Contains(code1, suggText);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            Assert.True(await db.AuditLogs.AnyAsync(a => a.ActorUserId == hrUser.Id && a.Action == "HR_DIRECTORY_IMPORT"));
        }
    }

    [Fact]
    public async Task M1_10_VietnameseDirectorySearch_UnaccentPgTrgm_DepartmentFilter_AndHidesInactive()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var suffix = Guid.NewGuid().ToString("N")[..6];
        const string pw = "Directory-User-Strong-2026!";

        Guid engDeptId;
        Guid hrDeptId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var opts = scope.ServiceProvider.GetRequiredService<IOptions<IdentityModuleOptions>>().Value;
            var org = await IdentityBootstrapService.EnsureDefaultOrganizationAsync(db, opts);

            var eng = new DepartmentEntity { Id = Guid.CreateVersion7(), OrganizationId = org.Id, Code = $"DE{suffix}", Name = $"Engineering {suffix}", CreatedAt = DateTimeOffset.UtcNow };
            var hr = new DepartmentEntity { Id = Guid.CreateVersion7(), OrganizationId = org.Id, Code = $"DH{suffix}", Name = $"HR {suffix}", CreatedAt = DateTimeOffset.UtcNow };
            db.Departments.AddRange(eng, hr);
            await db.SaveChangesAsync();
            engDeptId = eng.Id;
            hrDeptId = hr.Id;
        }

        var searcherUser = $"searcher_{suffix}";
        await SeedVerifiedUserAsync(app.Services, searcherUser, pw, "Phạm Minh Anh", OrgRole.Member, deptId: hrDeptId);
        await SeedVerifiedUserAsync(app.Services, $"an_{suffix}", pw, $"Nguyễn Văn An {suffix}", OrgRole.Member, deptId: engDeptId);
        await SeedVerifiedUserAsync(app.Services, $"dang_{suffix}", pw, $"Đặng Quốc Bảo {suffix}", OrgRole.Member, deptId: engDeptId);
        await SeedVerifiedUserAsync(app.Services, $"bich_{suffix}", pw, $"Trần Thị Bích {suffix}", OrgRole.Member, deptId: hrDeptId);
        await SeedVerifiedUserAsync(app.Services, $"dis_{suffix}", pw, $"Nguyễn Văn An Disabled {suffix}", OrgRole.Member, deptId: engDeptId, status: AccountStatus.Disabled);
        await SeedVerifiedUserAsync(app.Services, $"frm_{suffix}", pw, $"Nguyễn Văn An Former {suffix}", OrgRole.Member, deptId: engDeptId, status: AccountStatus.Former);

        var (_, client) = CreateCookieClient(app);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { username = searcherUser, password = pw });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // 1. Diacritic-insensitive Vietnamese search via PostgreSQL unaccent + pg_trgm
        using var r1 = await client.GetAsync($"/api/people/directory?q={Uri.EscapeDataString($"nguyen van an {suffix}")}");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var items1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().ToList();
        var names1 = items1.Select(x => x.GetProperty("displayName").GetString()).ToList();
        Assert.Contains($"Nguyễn Văn An {suffix}", names1);
        Assert.DoesNotContain($"Nguyễn Văn An Disabled {suffix}", names1);
        Assert.DoesNotContain($"Nguyễn Văn An Former {suffix}", names1);

        // 2. Vietnamese Đ/đ normalization search ("dang quoc bao")
        using var r2 = await client.GetAsync($"/api/people/directory?q={Uri.EscapeDataString($"dang quoc bao {suffix}")}");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var names2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync()).RootElement.GetProperty("items")
            .EnumerateArray().Select(x => x.GetProperty("displayName").GetString()).ToList();
        Assert.Contains($"Đặng Quốc Bảo {suffix}", names2);

        // 3. Department filter isolates Engineering from HR
        using var r3 = await client.GetAsync($"/api/people/directory?departmentId={engDeptId}");
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
        var names3 = JsonDocument.Parse(await r3.Content.ReadAsStringAsync()).RootElement.GetProperty("items")
            .EnumerateArray().Select(x => x.GetProperty("displayName").GetString()).ToList();
        Assert.Contains($"Nguyễn Văn An {suffix}", names3);
        Assert.Contains($"Đặng Quốc Bảo {suffix}", names3);
        Assert.DoesNotContain($"Trần Thị Bích {suffix}", names3);

        // 4. Pagination hard cap (max 50) enforced
        using var badLimit = await client.GetAsync("/api/people/directory?limit=51");
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
    }

    [Fact]
    public async Task M1_11_CustomStatus_AndReencodedAvatar_StripsExifAndRejectsSpoofedFiles()
    {
        var tempStorage = Path.Combine(Path.GetTempPath(), "nexus-m1b-avatar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStorage);

        try
        {
            await using var app = BuildHost($"--NexusStorage:LocalRootPath={tempStorage}");
            await app.StartAsync();

            var suffix = Guid.NewGuid().ToString("N")[..6];
            var username = $"prof_{suffix}";
            const string pw = "Profile-Strong-Pass-2026!";
            var user = await SeedVerifiedUserAsync(app.Services, username, pw, $"Profile User {suffix}", OrgRole.Member);

            var (_, client) = CreateCookieClient(app);
            using var anonClient = new HttpClient { BaseAddress = new Uri(GetBaseAddress(app)) };

            using var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = pw });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            // 1. Custom status (UX-06) updates and appears in directory & /api/auth/me
            using var statusResp = await client.PutAsJsonAsync("/api/people/me/status", new { customStatus = "At construction site" });
            Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);

            using var meResp = await client.GetAsync($"/api/organizations/{await OrganizationFixtureHandler.GetIdAsync(app.Services)}/me");
            Assert.Equal("At construction site", JsonDocument.Parse(await meResp.Content.ReadAsStringAsync()).RootElement.GetProperty("customStatus").GetString());

            using var tooLongStatus = await client.PutAsJsonAsync("/api/people/me/status", new { customStatus = new string('x', 101) });
            Assert.Equal(HttpStatusCode.BadRequest, tooLongStatus.StatusCode);

            // 2. Spoofed non-image payload rejected
            using var fakeImgReq = new HttpRequestMessage(HttpMethod.Post, "/api/people/me/avatar")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<script>alert('xss')</script>"))
            };
            fakeImgReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            using var fakeImgResp = await client.SendAsync(fakeImgReq);
            Assert.Equal(HttpStatusCode.BadRequest, fakeImgResp.StatusCode);

            // 3. Valid 400x200 JPEG with embedded EXIF/COM metadata is re-encoded to 256x256 PNG with metadata stripped
            byte[] jpegWithMetadata;
            using (var srcBitmap = new SKBitmap(400, 200))
            {
                using (var canvas = new SKCanvas(srcBitmap))
                {
                    canvas.Clear(new SKColor(30, 112, 184));
                }
                using var srcImage = SKImage.FromBitmap(srcBitmap);
                using var encoded = srcImage.Encode(SKEncodedImageFormat.Jpeg, 90);
                var rawJpeg = encoded.ToArray();

                // Inject a JPEG COM segment (FF FE) carrying sensitive metadata right after SOI (FF D8)
                var secretComment = Encoding.ASCII.GetBytes("Sensitive-Internal-Camera-GPS");
                var segLen = secretComment.Length + 2;
                using var ms = new MemoryStream();
                ms.Write(rawJpeg, 0, 2); // FF D8
                ms.WriteByte(0xFF);
                ms.WriteByte(0xFE);
                ms.WriteByte((byte)(segLen >> 8));
                ms.WriteByte((byte)(segLen & 0xFF));
                ms.Write(secretComment);
                ms.Write(rawJpeg, 2, rawJpeg.Length - 2);
                jpegWithMetadata = ms.ToArray();
            }

            Assert.Contains("Sensitive-Internal-Camera-GPS", Encoding.Latin1.GetString(jpegWithMetadata));

            using var uploadReq = new HttpRequestMessage(HttpMethod.Post, "/api/people/me/avatar")
            {
                Content = new ByteArrayContent(jpegWithMetadata)
            };
            uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            using var uploadResp = await client.SendAsync(uploadReq);
            Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
            var avatarUrl = JsonDocument.Parse(await uploadResp.Content.ReadAsStringAsync()).RootElement.GetProperty("avatarUrl").GetString()!;

            // Anonymous download denied (401)
            using var anonAvatar = await anonClient.GetAsync(avatarUrl);
            Assert.Equal(HttpStatusCode.Unauthorized, anonAvatar.StatusCode);

            // Authenticated download returns clean 256x256 PNG with nosniff and stripped metadata
            using var getAvatar = await client.GetAsync(avatarUrl);
            Assert.Equal(HttpStatusCode.OK, getAvatar.StatusCode);
            Assert.Equal("image/png", getAvatar.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", Assert.Single(getAvatar.Headers.GetValues("X-Content-Type-Options")));

            var downloadedBytes = await getAvatar.Content.ReadAsByteArrayAsync();
            Assert.DoesNotContain("Sensitive-Internal-Camera-GPS", Encoding.Latin1.GetString(downloadedBytes));

            using (var codec = SKCodec.Create(new MemoryStream(downloadedBytes)))
            {
                Assert.NotNull(codec);
                Assert.Equal(SKEncodedImageFormat.Png, codec!.EncodedFormat);
                Assert.Equal(AvatarProcessor.AvatarDimensionPx, codec.Info.Width);
                Assert.Equal(AvatarProcessor.AvatarDimensionPx, codec.Info.Height);
            }

            // Delete avatar removes it
            using var delResp = await client.DeleteAsync("/api/people/me/avatar");
            Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

            using var afterDelete = await client.GetAsync($"/api/people/{user.Id}/avatar");
            Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tempStorage))
            {
                Directory.Delete(tempStorage, recursive: true);
            }
        }
    }

    private WebApplication BuildHost(params string[] extraArgs)
    {
        var args = new List<string>
        {
            "--urls", "http://127.0.0.1:0",
            "--NexusDatabase:RequireTls=false",
            $"--ConnectionStrings:Runtime={pg.RuntimeConnectionString}"
        };
        args.AddRange(extraArgs);
        return NexusHost.Build([.. args]);
    }

    private static async Task<NexusUser> SeedVerifiedUserAsync(
        IServiceProvider services,
        string username,
        string password,
        string displayName,
        OrgRole role,
        Guid? deptId = null,
        AccountStatus status = AccountStatus.Active)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NexusUser>>();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<IdentityModuleOptions>>().Value;
        var org = await IdentityBootstrapService.EnsureDefaultOrganizationAsync(db, opts);

        var user = new NexusUser
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = org.Id,
            UserName = username,
            DisplayName = displayName,
            Status = status,
            TrustLevel = TrustLevel.Verified,
            OrgRole = role,
            DepartmentId = deptId,
            TermsVersion = opts.CurrentTermsVersion,
            TermsAcceptedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var res = await userManager.CreateAsync(user, password);
        Assert.True(res.Succeeded, string.Join("; ", res.Errors.Select(e => e.Description)));
        OrganizationFixtureHandler.Add(db, org.Id, user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<HttpClient> LoginAndEnableMfaAsync(WebApplication app, string username, string password)
    {
        var (_, client) = CreateCookieClient(app);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var setup = await client.PostAsync("/api/auth/mfa/setup", null);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var sharedKey = JsonDocument.Parse(await setup.Content.ReadAsStringAsync()).RootElement.GetProperty("sharedKey").GetString()!;

        using var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable", new { code = GenerateTotpCode(sharedKey) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        return client;
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

    private static string GenerateTotpCode(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleaned = base32Key.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
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

        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(timestep);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

#pragma warning disable CA5350 // RFC 6238 TOTP used by ASP.NET Core Identity requires HMAC-SHA1 by specification.
        using var hmac = new HMACSHA1([.. output]);
#pragma warning restore CA5350
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6");
    }
}
