using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Identity;
using Nexus.Web.Modules.Org;

namespace Nexus.IntegrationTests;

internal sealed class OrganizationFixtureHandler(IServiceProvider services, HttpMessageHandler transport) : DelegatingHandler(transport)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is ["api", "people" or "communities" or "conversations" or "org" or "admin" or "channels" or "posts" or "polls" or "files" or "sync", ..])
        {
            var organizationId = await GetIdAsync(services, cancellationToken);
            request.RequestUri = new Uri(uri.GetLeftPart(UriPartial.Authority) + $"/api/organizations/{organizationId}" + uri.PathAndQuery[4..]);
        }
        return await base.SendAsync(request, cancellationToken);
    }

    public static async Task<Guid> GetIdAsync(IServiceProvider provider, CancellationToken ct = default)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityModuleOptions>>().Value;
        return (await IdentityBootstrapService.EnsureDefaultOrganizationAsync(db, options, ct)).Id;
    }

    public static OrganizationMembership Add(NexusDbContext db, Guid organizationId, NexusUser account)
    {
        var member = OrganizationMembership.Create(organizationId, account, account.CreatedAt);
        member.DepartmentId = account.DepartmentId;
        member.BranchId = account.BranchId;
        member.EmployeeId = account.EmployeeId;
        member.OrgRole = account.OrgRole;
        member.TrustLevel = account.TrustLevel;
        member.Status = account.Status;
        db.OrganizationMemberships.Add(member);
        return member;
    }

    public static async Task AdmitAsync(IServiceProvider provider, Guid userId)
    {
        var orgId = await GetIdAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var account = await db.Users.SingleAsync(u => u.Id == userId);
        Add(db, orgId, account);
        await db.SaveChangesAsync();
    }
}
