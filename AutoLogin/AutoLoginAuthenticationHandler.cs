namespace UmbracoMemoryUsage.AutoLogin;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Security;

internal sealed class AutoLoginAuthenticationHandler
(
    IOptionsMonitor<AutoLoginOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IBackOfficeUserManager userManager,
    IBackOfficeSignInManager signInManager
) : RemoteAuthenticationHandler<AutoLoginOptions>(options, logger, encoder)
{
    private const string _returnUrl = "/umbraco";

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Redirect(Options.CallbackPath);
        return Task.CompletedTask;
    }

    protected override async Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
    {
        BackOfficeIdentityUser identityUser = await userManager.FindByEmailAsync(Options.UserEmail)
            ?? throw new InvalidOperationException($"The user with email address {Options.UserEmail} could not be found");

        AuthenticationProperties properties = signInManager.ConfigureExternalAuthenticationProperties(
            AutoLoginOptions.AuthenticationScheme,
            _returnUrl,
            identityUser.Id);

        ClaimsPrincipal principal = await signInManager.CreateUserPrincipalAsync(identityUser);
        AuthenticationTicket ticket = new(principal, properties, Constants.Security.BackOfficeExternalAuthenticationType);

        return HandleRequestResult.Success(ticket);
    }
}
