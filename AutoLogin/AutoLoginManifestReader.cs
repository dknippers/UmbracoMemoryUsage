namespace UmbracoMemoryUsage.AutoLogin;

using System.Collections.Generic;
using System.Threading.Tasks;
using Umbraco.Cms.Core.Manifest;
using Umbraco.Cms.Infrastructure.Manifest;

public class AutoLoginManifestReader : IPackageManifestReader
{
    public Task<IEnumerable<PackageManifest>> ReadPackageManifestsAsync()
    {
        IEnumerable<PackageManifest> manifests =
        [
            new PackageManifest()
            {
                Name = "AutoLogin",
                AllowPublicAccess = true,
                Extensions =
                [
                    new
                    {
                        alias = "AutoLogin",
                        type = "authProvider",
                        name = "AutoLogin",
                        forProviderName = AutoLoginOptions.AuthenticationScheme,
                        meta = new
                        {
                            label = AutoLoginOptions.DisplayName,
                            behavior = new
                            {
                                autoRedirect = true
                            },
                            linking = new
                            {
                                allowManualLinking = false
                            }
                        }
                    }
                ]
            }
        ];

        return Task.FromResult(manifests);
    }
}