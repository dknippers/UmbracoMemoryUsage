namespace UmbracoMemoryUsage.AutoLogin;

using Umbraco.Cms.Infrastructure.Manifest;

internal static class AutoLoginUmbracoBuilderExtensions
{
    /// <summary>
    /// Adds the developer auto login.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="userEmail">The user email.</param>
    /// <returns></returns>
    public static IUmbracoBuilder AddAutoLogin(this IUmbracoBuilder builder, string userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
        {
            throw new InvalidOperationException("User email must be provided for AutoLogin.");
        }

        builder.Services.ConfigureOptions<AutoLoginProviderOptions>();
        builder.Services.AddSingleton<IPackageManifestReader, AutoLoginManifestReader>();

        builder.AddBackOfficeExternalLogins(logins =>
        {
            logins.AddBackOfficeLogin(authBuilder =>
            {
                var scheme = AutoLoginOptions.AuthenticationScheme;
                var displayName = AutoLoginOptions.DisplayName;

                authBuilder.AddRemoteScheme<AutoLoginOptions, AutoLoginAuthenticationHandler>(scheme, displayName, options =>
                {
                    options.CallbackPath = "/umbraco-dev-auto-login";
                    options.UserEmail = userEmail;
                });
            });
        });

        return builder;
    }
}
