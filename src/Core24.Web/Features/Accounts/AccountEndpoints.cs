using System.Security.Claims;
using Core24.Web.Data;

namespace Core24.Web.Features.Accounts;

public static class AccountEndpoints
{
    /// <summary>
    /// "Who am I?" for the static landing page's account menu. Reads the login cookie like
    /// any page does; returns only what the menu shows.
    /// </summary>
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/me", (ClaimsPrincipal user, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store"; // never cache one person's answer for another
            var signedIn = user.Identity?.IsAuthenticated == true;
            return Results.Ok(new
            {
                signedIn,
                email = signedIn ? user.Identity!.Name : null,
                isAdmin = signedIn && user.IsInRole(Roles.Admin),
            });
        });
}
