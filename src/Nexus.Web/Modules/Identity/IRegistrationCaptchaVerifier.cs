using Microsoft.Extensions.Options;

namespace Nexus.Web.Modules.Identity;

public interface IRegistrationCaptchaVerifier
{
    Task<bool> VerifyAsync(string? captchaToken, string? remoteIp, CancellationToken cancellationToken = default);
}

public sealed class SwitchableRegistrationCaptchaVerifier(IOptions<IdentityModuleOptions> options)
    : IRegistrationCaptchaVerifier
{
    public Task<bool> VerifyAsync(string? captchaToken, string? remoteIp, CancellationToken cancellationToken = default)
    {
        if (!options.Value.CaptchaEnabled)
        {
            return Task.FromResult(true);
        }

        // Fail closed when CAPTCHA is enabled without a concrete verifier or token.
        return Task.FromResult(false);
    }
}
