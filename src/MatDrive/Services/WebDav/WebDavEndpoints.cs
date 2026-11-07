using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MatDrive.Services.WebDav;

/// <summary>The registration of the WebDAV server: its services and the endpoint <c>/dav/</c>.</summary>
public static class WebDavEndpoints
{
    /// <summary>The locks (in memory, one set for the app) and the sign-in with its cache.</summary>
    public static IServiceCollection AddWebDav(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DavLockManager>();
        services.AddSingleton<DavAuthenticator>();
        return services;
    }

    /// <summary>
    /// <c>/dav</c> and everything below it, every method. Anonymous for the cookie sign-in of the pages (no redirect to the sign-in page, no anti-forgery
    /// token): WebDAV signs in with HTTP Basic itself (<see cref="WebDavHandler"/>).
    /// </summary>
    public static IEndpointConventionBuilder MapWebDav(this IEndpointRouteBuilder app)
        => app.Map("/dav/{**path}", WebDavHandler.HandleAsync)
            .AllowAnonymous()
            .WithMetadata(new DisableRequestSizeLimitAttribute())
            .WithDisplayName("WebDAV");
}
