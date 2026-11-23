using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Admin;
using Nexus.Web.Modules.Org;

namespace Nexus.Web.Modules.Identity;

public static class IdentityBootstrapService
{
    public static async Task<OrganizationEntity> EnsureDefaultOrganizationAsync(
        NexusDbContext db,
        IdentityModuleOptions options,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.Organizations
            .OrderBy(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var org = new OrganizationEntity
        {
            Id = Guid.CreateVersion7(),
            Name = options.DefaultOrganizationName,
            Slug = options.DefaultOrganizationSlug,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Organizations.Add(org);
        await db.SaveChangesAsync(cancellationToken);
        return org;
    }

    public static async Task<NexusUser> BootstrapSystemAdminAsync(
        IServiceProvider services,
        string username,
        string password,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (!UsernamePolicy.TryValidate(username, out var normalizedUsername, out var usernameError))
        {
            throw new InvalidOperationException(usernameError);
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<NexusUser>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityModuleOptions>>().Value;

        var org = await EnsureDefaultOrganizationAsync(db, options, cancellationToken);
        var existing = await userManager.FindByNameAsync(normalizedUsername);
        if (existing is not null)
        {
            return existing;
        }

        var admin = new NexusUser
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = org.Id,
            UserName = normalizedUsername,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedUsername : displayName.Trim(),
            Status = AccountStatus.Active,
            TrustLevel = TrustLevel.Verified,
            OrgRole = OrgRole.SystemAdmin,
            TermsVersion = options.CurrentTermsVersion,
            TermsAcceptedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            var reason = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to bootstrap SystemAdmin: {reason}");
        }

        if (!org.LegacyReviewPending && !await db.OrganizationMemberships.AnyAsync(m => m.OrganizationId == org.Id && m.IsOwner, cancellationToken))
        {
            var member = OrganizationMembership.Create(org.Id, admin, admin.CreatedAt);
            member.IsOwner = true;
            member.TrustLevel = TrustLevel.Verified;
            member.OrgRole = OrgRole.SystemAdmin;
            db.OrganizationMemberships.Add(member);
            await db.SaveChangesAsync(cancellationToken);
        }

        return admin;
    }

    public static async Task<string> IssueAccountRecoveryAsync(IServiceProvider services, string username, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<NexusUser>>();
        var account = await users.FindByNameAsync(username) ?? throw new InvalidOperationException("Account was not found.");
        if (account.Status != AccountStatus.Active) throw new InvalidOperationException("Account is not active.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        db.AdminPasswordResetLinks.Add(new AdminPasswordResetLinkEntity
        {
            Id = Guid.CreateVersion7(), TargetUserId = account.Id, IssuedByUserId = account.Id, CreatedAt = now,
            ExpiresAt = now.AddMinutes(30), TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
        });
        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(), OrganizationId = Guid.Empty, Action = "OPERATOR_ACCOUNT_RESET_ISSUED",
            ResourceType = "Account", ResourceId = account.Id.ToString(), Result = "Allowed", CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return token;
    }
}
