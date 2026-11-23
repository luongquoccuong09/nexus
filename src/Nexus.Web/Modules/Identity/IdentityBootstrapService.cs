using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
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

        return admin;
    }
}
