namespace UmbracoMemoryUsage.AutoLogin;

using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Security;

public class AutoLoginProviderOptions : IConfigureNamedOptions<BackOfficeExternalLoginProviderOptions>
{
    public void Configure(string? name, BackOfficeExternalLoginProviderOptions options)
    {
        if (!string.Equals(name, AutoLoginOptions.AuthenticationScheme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Configure(options);
    }

    public void Configure(BackOfficeExternalLoginProviderOptions options)
    {
        options.AutoLinkOptions = new ExternalSignInAutoLinkOptions(true);
    }
}
