namespace Nexus.Web.Modules.Identity;

public enum RegistrationMode
{
    Open,
    OpenWithJoinCode,
    InviteOnly,
    Approval
}

public sealed class IdentityModuleOptions
{
    public const string SectionName = "NexusIdentity";
    public const string TrialConfidentialityNotice =
        "Trial environment: keep confidential information out of Nexus, respect privacy, and use your own identity.";

    public RegistrationMode RegistrationMode { get; set; } = RegistrationMode.Open;
    public string? JoinCode { get; set; }
    public string CurrentTermsVersion { get; set; } = "trial-v1";
    public string DefaultOrganizationName { get; set; } = "Nexus Organization";
    public string DefaultOrganizationSlug { get; set; } = "default";
    public int DailyNewDmLimitForUnverified { get; set; } = 5;
    public bool CaptchaEnabled { get; set; }
    public int RegisterRateLimitPerMinutePerIp { get; set; } = 10;
    public int LoginRateLimitPerMinutePerIp { get; set; } = 20;
    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int ResetLinkExpiryMinutes { get; set; } = 30;
}
