namespace UmbracoMemoryUsage.AutoLogin;

using Microsoft.AspNetCore.Authentication;
using Umbraco.Cms.Api.Management.Security;

internal sealed class AutoLoginOptions : RemoteAuthenticationOptions
{
    public static readonly string AuthenticationScheme
        = BackOfficeAuthenticationBuilder.SchemeForBackOffice("AutoLogin")!;

    public const string DisplayName = "AutoLogin";

    public string UserEmail { get; set; } = null!; // We will ensure this is set to a non-null value.
}
