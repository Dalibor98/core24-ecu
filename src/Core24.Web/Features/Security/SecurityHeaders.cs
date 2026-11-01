namespace Core24.Web.Features.Security;

/// <summary>
/// HTTP headers that tell the browser to switch on its own protections. They cost nothing and
/// turn whole classes of attacks (clickjacking, injected scripts, MIME confusion) into no-ops.
/// </summary>
public static class SecurityHeaders
{
    // The app's own pages: only our own scripts. Styles may be inline (a few style="" attributes),
    // fonts come from Google. form-action must allow Stripe: after "Pay" the browser is redirected
    // there, and browsers apply form-action to that redirect too.
    private const string AppPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "form-action 'self' https://checkout.stripe.com; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "object-src 'none'";

    // The landing page is a static file with large inline scripts (the 3D scene), libraries from
    // two CDNs, and an HDR image from Poly Haven. It needs a looser script policy. It has no forms
    // that post anywhere except our own /api/contact (sent with fetch, so connect-src covers it).
    private const string LandingPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self' https://cdn.jsdelivr.net https://dl.polyhaven.org; " +
        "worker-src 'self' blob:; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // Set before calling next(): once the body starts, headers can't change any more.
            var h = context.Response.Headers;
            var path = context.Request.Path;
            var isLanding = path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

            h.ContentSecurityPolicy = isLanding ? LandingPolicy : AppPolicy;
            h.XContentTypeOptions = "nosniff";       // a .bin download is never "guessed" to be HTML or a script
            h.XFrameOptions = "DENY";                // old browsers' version of frame-ancestors 'none' (clickjacking)
            if (!h.ContainsKey("Referrer-Policy"))   // order pages set the stricter "no-referrer" themselves
                h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";

            await next();
        });
}
