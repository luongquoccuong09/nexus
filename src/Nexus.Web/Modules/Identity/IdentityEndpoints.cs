using Nexus.Web.Modules.Org;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;
using Nexus.Web.Modules.Admin;

namespace Nexus.Web.Modules.Identity;

public static class IdentityEndpoints
{
    public sealed record RegisterRequest(
        string Username,
        string Password,
        string DisplayName,
        bool TermsAccepted,
        string? JoinCode = null,
        string? Honeypot = null,
        string? CaptchaToken = null);

    public sealed record LoginRequest(
        string Username,
        string Password,
        string? TotpCode = null);

    public sealed record ChangePasswordRequest(
        string CurrentPassword,
        string NewPassword);

    public sealed record RedeemResetLinkRequest(
        string Token,
        string NewPassword);

    public sealed record EnableMfaRequest(string Code);
    public sealed record VerifyAccountRequest(string EmployeeCode);
    public sealed record RenameAccountRequest(string DisplayName);
    public sealed record ChangeAccountStatusRequest(string Status);

    public sealed record UserSummaryDto(
        Guid Id,
        string Username,
        string DisplayName,
        string Status,
        string TrustLevel,
        bool IsUnverified,
        string OrgRole,
        Guid? EmployeeId,
        bool MfaEnabled,
        bool MfaRequired,
        bool MfaSetupRequired,
        string? CustomStatus,
        string? AvatarUrl,
        AvailabilityDto Availability,
        bool SuppressChime);

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/csrf", (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(ctx);
            return Results.Ok(new { token = tokens.RequestToken });
        }).AllowAnonymous();

        auth.MapGet("/registration-policy", (IOptions<IdentityModuleOptions> opts) =>
        {
            var o = opts.Value;
            return Results.Ok(new
            {
                mode = o.RegistrationMode.ToString(),
                captchaEnabled = o.CaptchaEnabled,
                termsVersion = o.CurrentTermsVersion,
                confidentialityNotice = IdentityModuleOptions.TrialConfidentialityNotice,
                dailyNewDmLimitForUnverified = o.DailyNewDmLimitForUnverified
            });
        }).AllowAnonymous();

        auth.MapPost("/register", HandleRegisterAsync)
            .AllowAnonymous()
            .RequireRateLimiting(IdentityModuleExtensions.RegisterRateLimitPolicy);

        auth.MapPost("/login", HandleLoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(IdentityModuleExtensions.LoginRateLimitPolicy);

        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(SecurityDefaultsExtensions.SessionAuthScheme);
            return Results.NoContent();
        });
        auth.MapGet("/me", HandleMeAsync);
        auth.MapPost("/change-password", HandleChangePasswordAsync);
        auth.MapPost("/reset-password", HandleRedeemResetLinkAsync).AllowAnonymous();

        auth.MapGet("/sessions", HandleListSessionsAsync);
        auth.MapDelete("/sessions/{sessionId}", HandleRevokeSessionAsync);
        auth.MapPost("/sessions/revoke-others", HandleRevokeOtherSessionsAsync);

        auth.MapPost("/mfa/setup", HandleMfaSetupAsync);
        auth.MapPost("/mfa/enable", HandleMfaEnableAsync);

        var admin = app.MapGroup(OrganizationScope.Api + "/admin/accounts");
        admin.MapGet("/", HandleAdminListAccountsAsync);
        admin.MapPost("/{targetUserId:guid}/verify", HandleAdminVerifyAccountAsync);
        admin.MapPost("/{targetUserId:guid}/rename", HandleAdminRenameAccountAsync);
        admin.MapPost("/{targetUserId:guid}/status", HandleAdminChangeStatusAsync);

        return app;
    }

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext ctx,
        RegisterRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IRegistrationCaptchaVerifier captchaVerifier,
        IOptions<IdentityModuleOptions> optionsAccessor,
        CancellationToken ct)
    {
        var opts = optionsAccessor.Value;

        if (!string.IsNullOrEmpty(request.Honeypot))
        {
            return Results.BadRequest(new { code = "invalid_registration", message = "Registration rejected." });
        }

        if (opts.RegistrationMode is RegistrationMode.InviteOnly or RegistrationMode.Approval)
        {
            return Results.Json(
                new { code = "registration_closed", message = "Open registration is currently disabled." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (opts.RegistrationMode == RegistrationMode.OpenWithJoinCode &&
            (string.IsNullOrWhiteSpace(opts.JoinCode) ||
             !CryptographicEquals(request.JoinCode ?? string.Empty, opts.JoinCode)))
        {
            return Results.Json(
                new { code = "invalid_join_code", message = "A valid join code is required to register." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var captchaOk = await captchaVerifier.VerifyAsync(
            request.CaptchaToken,
            ctx.Connection.RemoteIpAddress?.ToString(),
            ct);
        if (!captchaOk)
        {
            return Results.BadRequest(new { code = "captcha_failed", message = "Verification challenge failed." });
        }

        if (!request.TermsAccepted)
        {
            return Results.BadRequest(new { code = "terms_required", message = "Accept the terms of use to create an account." });
        }

        if (!UsernamePolicy.TryValidate(request.Username, out var normalizedUsername, out var usernameError))
        {
            return Results.BadRequest(new { code = "invalid_username", message = usernameError });
        }

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length is < 1 or > 80)
        {
            return Results.BadRequest(new { code = "invalid_display_name", message = "Display name must be 1 to 80 characters." });
        }

        var existing = await userManager.FindByNameAsync(normalizedUsername);
        if (existing is not null)
        {
            return Results.Conflict(new { code = "username_taken", message = "This username is already taken." });
        }

        var now = DateTimeOffset.UtcNow;

        var user = new NexusUser
        {
            Id = Guid.CreateVersion7(),
            UserName = normalizedUsername,
            DisplayName = displayName,
            Status = AccountStatus.Active,
            TermsVersion = opts.CurrentTermsVersion,
            TermsAcceptedAt = now,
            CreatedAt = now
        };

        var createResult = await userManager.CreateAsync(user, request.Password ?? string.Empty);
        if (!createResult.Succeeded)
        {
            var message = string.Join(" ", createResult.Errors.Select(e => e.Description));
            return Results.BadRequest(new { code = "password_policy_failed", message });
        }

        await SignInSessionAsync(ctx, user, mfaVerified: false);
        await WriteAuditAsync(db, Guid.Empty, user.Id, "ACCOUNT_REGISTER", "UserAccount", user.Id.ToString(), "Allowed", null, ctx, ct);

        return Results.Created($"/api/auth/me", ToSummaryDto(user));
    }

    private static async Task<IResult> HandleLoginAsync(
        HttpContext ctx,
        LoginRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        CancellationToken ct)
    {
        static IResult GenericInvalid() => Results.Json(
            new { code = "invalid_credentials", message = "Invalid username or password." },
            statusCode: StatusCodes.Status401Unauthorized);

        var normalized = (request.Username ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrEmpty(request.Password))
        {
            return GenericInvalid();
        }

        var user = await userManager.FindByNameAsync(normalized);
        if (user is null)
        {
            return GenericInvalid();
        }

        if (user.Status != AccountStatus.Active || await userManager.IsLockedOutAsync(user))
        {
            return GenericInvalid();
        }

        var passwordValid = await userManager.CheckPasswordAsync(user, request.Password);
        if (!passwordValid)
        {
            await userManager.AccessFailedAsync(user);
            return GenericInvalid();
        }

        await userManager.ResetAccessFailedCountAsync(user);

        var mfaVerified = false;
        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.TotpCode))
            {
                return Results.Json(
                    new { code = "mfa_required", requiresMfa = true, message = "Enter your authenticator code." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var cleanedCode = request.TotpCode.Replace(" ", string.Empty, StringComparison.Ordinal)
                                              .Replace("-", string.Empty, StringComparison.Ordinal);
            var totpOk = await userManager.VerifyTwoFactorTokenAsync(
                user,
                TokenOptions.DefaultAuthenticatorProvider,
                cleanedCode);

            if (!totpOk)
            {
                var recoveryResult = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, request.TotpCode.Trim());
                if (!recoveryResult.Succeeded)
                {
                    await userManager.AccessFailedAsync(user);
                    return GenericInvalid();
                }
            }

            mfaVerified = true;
        }

        await SignInSessionAsync(ctx, user, mfaVerified);
        await WriteAuditAsync(db, Guid.Empty, user.Id, "AUTH_PASSWORD_LOGIN", "UserAccount", user.Id.ToString(), "Allowed", null, ctx, ct);

        var requiresMfa = await HasActiveAdminRoleAsync(db, user.Id, ct);
        return Results.Ok(ToSummaryDto(user, requiresMfa));
    }

    private static async Task<IResult> HandleMeAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var requiresMfa = await HasActiveAdminRoleAsync(db, user.Id, ct);
        return Results.Ok(ToSummaryDto(user, requiresMfa));
    }

    private static async Task<IResult> HandleChangePasswordAsync(
        HttpContext ctx,
        ChangePasswordRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IUserConnectionRegistry connectionRegistry,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var result = await userManager.ChangePasswordAsync(
            user,
            request.CurrentPassword ?? string.Empty,
            request.NewPassword ?? string.Empty);

        if (!result.Succeeded)
        {
            var message = string.Join(" ", result.Errors.Select(e => e.Description));
            return Results.BadRequest(new { code = "change_password_failed", message });
        }

        await userManager.UpdateSecurityStampAsync(user);

        var currentSessionId = ctx.User.GetSessionId();
        var now = DateTimeOffset.UtcNow;

        // Revoke all OTHER sessions in PostgreSQL (M1.5 / SEC-24); current session is renewed in-place below.
        await db.AppSessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null && s.Id != currentSessionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now), ct);

        // Disconnect active SignalR sockets so stale sessions cannot remain connected.
        connectionRegistry.AbortUserConnections(user.Id);

        // Renew current session ticket with the rotated security stamp.
        var mfaVerified = string.Equals(ctx.User.FindFirstValue(ClaimsPrincipalExtensions.MfaVerifiedClaimType), "true", StringComparison.Ordinal);
        await SignInSessionAsync(ctx, user, mfaVerified);

        await WriteAuditAsync(db, Guid.Empty, user.Id, "PASSWORD_CHANGED", "UserAccount", user.Id.ToString(), "Allowed", null, ctx, ct);
        return Results.Ok(new { changed = true });
    }

    private static async Task<IResult> HandleRedeemResetLinkAsync(
        HttpContext ctx,
        RedeemResetLinkRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IUserConnectionRegistry connectionRegistry,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Results.BadRequest(new { code = "invalid_reset_token", message = "Reset token is invalid or expired." });
        }

        var tokenHash = ComputeSha256Hex(request.Token.Trim());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var link = await db.AdminPasswordResetLinks.FromSqlInterpolated($"""
            SELECT * FROM admin_reset_links WHERE token_hash = {tokenHash} FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;

        if (link is null || link.UsedAt is not null || link.ExpiresAt <= now)
        {
            return Results.BadRequest(new { code = "invalid_reset_token", message = "Reset token is invalid or expired." });
        }

        var user = await userManager.FindByIdAsync(link.TargetUserId.ToString());
        if (user is null || user.Status != AccountStatus.Active)
        {
            return Results.BadRequest(new { code = "invalid_reset_token", message = "Reset token is invalid or expired." });
        }

        // Validate new password against all registered validators before replacing.
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var resetResult = await userManager.ResetPasswordAsync(user, resetToken, request.NewPassword ?? string.Empty);
        if (!resetResult.Succeeded)
        {
            var message = string.Join(" ", resetResult.Errors.Select(e => e.Description));
            return Results.BadRequest(new { code = "password_policy_failed", message });
        }

        link.UsedAt = now;
        await db.SaveChangesAsync(ct);

        // Commit the credential, the redeemed token, and session revocation together.
        await db.AppSessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now), ct);
        await WriteAuditAsync(db, Guid.Empty, user.Id, "PASSWORD_RESET_REDEEMED", "UserAccount", user.Id.ToString(), "Allowed", null, ctx, ct);
        await transaction.CommitAsync(ct);
        connectionRegistry.AbortUserConnections(user.Id);
        return Results.Ok(new { reset = true });
    }

    private static async Task<IResult> HandleListSessionsAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        int? limit,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var currentSessionId = ctx.User.GetSessionId();
        var now = DateTimeOffset.UtcNow;

        var items = await db.AppSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && (s.ExpiresAt == null || s.ExpiresAt > now))
            .OrderByDescending(s => s.LastActivityAt)
            .Take(pageSize)
            .Select(s => new
            {
                id = s.Id,
                userAgent = s.UserAgent ?? "Unknown device",
                ipAddress = s.IpAddress ?? "Unknown",
                createdAt = s.CreatedAt,
                lastActivityAt = s.LastActivityAt,
                isCurrent = s.Id == currentSessionId
            })
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }

    private static async Task<IResult> HandleRevokeSessionAsync(
        HttpContext ctx,
        string sessionId,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow;
        var updated = await db.AppSessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now), ct);

        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> HandleRevokeOtherSessionsAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var currentSessionId = ctx.User.GetSessionId();
        var now = DateTimeOffset.UtcNow;

        var count = await db.AppSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != currentSessionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now), ct);

        return Results.Ok(new { revokedCount = count });
    }

    private static async Task<IResult> HandleMfaSetupAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = await userManager.GetAuthenticatorKeyAsync(user);

            // ResetAuthenticatorKeyAsync rotates SecurityStamp; renew current session ticket in-place.
            var mfaVerified = string.Equals(ctx.User.FindFirstValue(ClaimsPrincipalExtensions.MfaVerifiedClaimType), "true", StringComparison.Ordinal);
            await SignInSessionAsync(ctx, user, mfaVerified);
        }

        var issuer = Uri.EscapeDataString("Project Nexus");
        var label = Uri.EscapeDataString(user.UserName ?? user.Id.ToString());
        var otpAuthUri = $"otpauth://totp/{issuer}:{label}?secret={key}&issuer={issuer}&digits=6";

        return Results.Ok(new
        {
            sharedKey = key,
            otpAuthUri,
            mfaEnabled = user.TwoFactorEnabled
        });
    }

    private static async Task<IResult> HandleMfaEnableAsync(
        HttpContext ctx,
        EnableMfaRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var userId))
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var cleanedCode = (request.Code ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user,
            TokenOptions.DefaultAuthenticatorProvider,
            cleanedCode);

        if (!valid)
        {
            return Results.BadRequest(new { code = "invalid_totp", message = "Authenticator code is invalid." });
        }

        await userManager.SetTwoFactorEnabledAsync(user, true);
        var recoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 8))?.ToArray() ?? [];

        // Renew current session ticket in-place with mfa_verified = true.
        await SignInSessionAsync(ctx, user, mfaVerified: true);

        await WriteAuditAsync(db, Guid.Empty, user.Id, "MFA_ENABLED", "UserAccount", user.Id.ToString(), "Allowed", null, ctx, ct);
        return Results.Ok(new { mfaEnabled = true, recoveryCodes });
    }

    private static async Task<IResult> HandleAdminListAccountsAsync(
        HttpContext ctx,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IAuthorizationService authz,
        int? limit,
        CancellationToken ct)
    {
        var (actorUser, guardResult) = await AuthorizeAdminAsync(
            ctx, db, authz, ResourceAction.AdminVerifyAccount,
            new ResourceContext(ResourceKind.UserAccount), requireMfa: true);
        if (guardResult is not null)
        {
            return guardResult;
        }

        if (!PaginationGuard.TryNormalize(limit, out var pageSize, out var pageError))
        {
            return Results.BadRequest(new { code = "invalid_page_size", message = pageError });
        }

        var items = await db.OrganizationPeople
            .AsNoTracking()
            .Include(u => u.Account)
            .Where(u => u.OrganizationId == actorUser!.OrganizationId)
            .OrderByDescending(u => u.CreatedAt)
            .Take(pageSize)
            .Select(u => ToMemberSummaryDto(u))
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }

    private static async Task<IResult> HandleAdminVerifyAccountAsync(
        HttpContext ctx,
        Guid targetUserId,
        VerifyAccountRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        var (actorUser, guardResult) = await AuthorizeAdminAsync(
            ctx, db, authz, ResourceAction.AdminVerifyAccount,
            new ResourceContext(ResourceKind.UserAccount), requireMfa: true);
        if (guardResult is not null)
        {
            return guardResult;
        }

        var target = await db.OrganizationPeople.SingleOrDefaultAsync(m => m.Id == targetUserId, ct);
        if (target is null || target.OrganizationId != actorUser!.OrganizationId)
        {
            return Results.NotFound();
        }

        var empCode = (request.EmployeeCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(empCode))
        {
            return Results.BadRequest(new { code = "invalid_employee_code", message = "Employee code is required." });
        }

        var employee = await db.Employees
            .SingleOrDefaultAsync(e => e.OrganizationId == actorUser.OrganizationId && e.EmployeeCode == empCode && e.IsActive, ct);
        if (employee is null)
        {
            return Results.BadRequest(new { code = "employee_not_found", message = "Active employee record not found." });
        }

        var alreadyLinked = await db.OrganizationPeople.AnyAsync(u => u.EmployeeId == employee.Id && u.Id != target.Id, ct);
        if (alreadyLinked)
        {
            return Results.Conflict(new { code = "employee_already_linked", message = "This employee record is already linked to another account." });
        }

        target.EmployeeId = employee.Id;
        target.DepartmentId = employee.DepartmentId;
        target.BranchId = employee.BranchId;
        target.TrustLevel = TrustLevel.Verified;
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            db, actorUser.OrganizationId, actorUser.Id, "ACCOUNT_VERIFY", "UserAccount", target.Id.ToString(), "Allowed",
            JsonSerializer.Serialize(new { employeeCode = empCode }), ctx, ct);

        return Results.Ok(ToMemberSummaryDto(target));
    }

    private static async Task<IResult> HandleAdminRenameAccountAsync(
        HttpContext ctx,
        Guid targetUserId,
        RenameAccountRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IAuthorizationService authz,
        CancellationToken ct)
    {
        var (actorUser, guardResult) = await AuthorizeAdminAsync(
            ctx, db, authz, ResourceAction.AdminVerifyAccount,
            new ResourceContext(ResourceKind.UserAccount), requireMfa: true);
        if (guardResult is not null)
        {
            return guardResult;
        }

        var target = await db.OrganizationPeople.SingleOrDefaultAsync(m => m.Id == targetUserId, ct);
        if (target is null || target.OrganizationId != actorUser!.OrganizationId)
        {
            return Results.NotFound();
        }

        var newName = (request.DisplayName ?? string.Empty).Trim();
        if (newName.Length is < 1 or > 80)
        {
            return Results.BadRequest(new { code = "invalid_display_name", message = "Display name must be 1 to 80 characters." });
        }

        var previousName = target.DisplayName;
        target.DisplayName = newName;
        await db.SaveChangesAsync(ct);

        await WriteAuditAsync(
            db, actorUser.OrganizationId, actorUser.Id, "ACCOUNT_RENAME", "UserAccount", target.Id.ToString(), "Allowed",
            JsonSerializer.Serialize(new { previousName, newName }), ctx, ct);

        return Results.Ok(ToMemberSummaryDto(target));
    }

    private static async Task<IResult> HandleAdminChangeStatusAsync(
        HttpContext ctx,
        Guid targetUserId,
        ChangeAccountStatusRequest request,
        [FromServices] NexusDbContext db,
        UserManager<NexusUser> userManager,
        IAuthorizationService authz,
        IUserConnectionRegistry connectionRegistry,
        CancellationToken ct)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return Results.Unauthorized();
        }

        var (actorUser, guardResult) = await AuthorizeAdminAsync(
            ctx, db, authz, ResourceAction.AdminDisableOrEnableAccount,
            new ResourceContext(ResourceKind.UserAccount, TargetIsSelf: actorId == targetUserId),
            requireMfa: true);
        if (guardResult is not null)
        {
            return guardResult;
        }

        if (!Enum.TryParse<AccountStatus>(request.Status, ignoreCase: true, out var nextStatus) ||
            nextStatus == AccountStatus.Former)
        {
            return Results.BadRequest(new { code = "invalid_status", message = "Status must be Active or Disabled." });
        }

        var target = await db.OrganizationPeople.SingleOrDefaultAsync(m => m.Id == targetUserId, ct);
        if (target is null || target.OrganizationId != actorUser!.OrganizationId)
        {
            return Results.NotFound();
        }

        if (target.IsOwner || target.Status == AccountStatus.Former) return Results.Forbid();
        target.Status = nextStatus;
        await db.SaveChangesAsync(ct);

        if (nextStatus != AccountStatus.Active)
        {
            var now = DateTimeOffset.UtcNow;
            connectionRegistry.AbortOrganizationConnections(target.OrganizationId, target.Id);
        }

        await WriteAuditAsync(
            db, actorUser.OrganizationId, actorUser.Id, "ACCOUNT_STATUS_CHANGE", "UserAccount", target.Id.ToString(), "Allowed",
            JsonSerializer.Serialize(new { status = nextStatus.ToString() }), ctx, ct);

        return Results.Ok(ToMemberSummaryDto(target));
    }

    private static async Task<(OrganizationMembership? Actor, IResult? ErrorResult)> AuthorizeAdminAsync(
        HttpContext ctx,
        NexusDbContext db,
        IAuthorizationService authz,
        ResourceAction action,
        ResourceContext resource,
        bool requireMfa)
    {
        if (!ctx.User.TryGetUserId(out var actorId))
        {
            return (null, Results.Unauthorized());
        }

        var actorUser = await db.FindOrganizationActorAsync(actorId, ctx.RequestAborted);
        if (actorUser is null)
        {
            return (null, Results.Unauthorized());
        }

        var scopedResource = resource with { OrganizationId = actorUser.OrganizationId };
        if (!authz.Can(actorUser.ToActorContext(), action, scopedResource))
        {
            return (null, Results.Forbid());
        }

        if (requireMfa)
        {
            var mfaClaim = string.Equals(
                ctx.User.FindFirstValue(ClaimsPrincipalExtensions.MfaVerifiedClaimType),
                "true",
                StringComparison.Ordinal);

            if (!mfaClaim)
            {
                return (null, Results.Json(
                    new { code = "admin_mfa_required", message = "TOTP multi-factor authentication is required for administrative actions." },
                    statusCode: StatusCodes.Status403Forbidden));
            }
        }

        return (actorUser, null);
    }

    private static async Task SignInSessionAsync(HttpContext ctx, NexusUser user, bool mfaVerified)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? user.Id.ToString()),
            new(ClaimsPrincipalExtensions.SecurityStampClaimType, user.SecurityStamp ?? string.Empty),
            new(ClaimsPrincipalExtensions.MfaVerifiedClaimType, mfaVerified ? "true" : "false")
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SecurityDefaultsExtensions.SessionAuthScheme));
        await ctx.SignInAsync(
            SecurityDefaultsExtensions.SessionAuthScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
            });
    }

    private static async Task WriteAuditAsync(
        NexusDbContext db,
        Guid orgId,
        Guid? actorUserId,
        string action,
        string resourceType,
        string resourceId,
        string result,
        string? detailJson,
        HttpContext ctx,
        CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLogEntity
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = orgId,
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Result = result,
            DetailJson = detailJson,
            IpAddress = ctx.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private static UserSummaryDto ToMemberSummaryDto(OrganizationMembership m)
    {
        var now = DateTimeOffset.UtcNow;
        var state = m.Account?.AvailabilityState ?? AvailabilityState.Available;
        var expiresAt = m.Account?.AvailabilityExpiresAt;
        return new(
            m.Id, m.UserName, m.DisplayName, m.Status.ToString(), m.TrustLevel.ToString(),
            m.TrustLevel == TrustLevel.Unverified, m.OrgRole.ToString(), m.EmployeeId, false, false, false,
            m.CustomStatus, m.AvatarObjectKey is null ? null : $"/api/organizations/{m.OrganizationId}/people/{m.Id}/avatar",
            AvailabilityResolver.Resolve(state, expiresAt, now),
            AvailabilityResolver.IsChimeSuppressed(state, expiresAt, now));
    }

    private static Task<bool> HasActiveAdminRoleAsync(NexusDbContext db, Guid userId, CancellationToken ct) =>
        db.OrganizationMemberships.AsNoTracking()
            .AnyAsync(m => m.Id == userId && m.Status == AccountStatus.Active && m.OrgRole != OrgRole.Member, ct);

    private static UserSummaryDto ToSummaryDto(NexusUser u, bool requiresMfa = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            u.Id,
            u.UserName ?? string.Empty,
            u.DisplayName,
            u.Status.ToString(),
            TrustLevel.Unverified.ToString(),
            true,
            OrgRole.Member.ToString(),
            null,
            u.TwoFactorEnabled,
            requiresMfa,
            requiresMfa && !u.TwoFactorEnabled,
            null,
            null,
            AvailabilityResolver.Resolve(u.AvailabilityState, u.AvailabilityExpiresAt, now),
            AvailabilityResolver.IsChimeSuppressed(u.AvailabilityState, u.AvailabilityExpiresAt, now));
    }

    private static string ComputeSha256Hex(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static bool CryptographicEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
