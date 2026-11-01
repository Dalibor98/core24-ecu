using System.ComponentModel.DataAnnotations;
using Core24.Web.Features.Notifications;

namespace Core24.Web.Features.Contact;

/// <summary>What the landing page's "Book a check" form sends, as JSON.</summary>
public sealed class ContactRequest
{
    [Required, StringLength(100)]
    public string Name { get; set; } = "";

    [Required, StringLength(40)]
    public string Phone { get; set; } = "";

    [StringLength(40)]
    public string? Make { get; set; }

    [StringLength(100)]
    public string? Model { get; set; }

    [StringLength(2000)]
    public string? Issue { get; set; }

    /// <summary>
    /// A "honeypot": a field hidden from people with CSS. Spam bots fill in every field they find,
    /// so a value here means a bot. We answer "OK" (so the bot learns nothing) and send nothing.
    /// </summary>
    public string? Website { get; set; }
}

public static class ContactEndpoint
{
    public static void MapContactEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/contact", HandleAsync)
           .RequireRateLimiting("emails"); // every request sends an email: limit it per IP

    private static async Task<IResult> HandleAsync(ContactRequest request, Notifier notifier, ILoggerFactory loggers, CancellationToken ct)
    {
        // No anti-forgery token, because the landing page is a static file and can't hold one.
        // That's acceptable here: minimal APIs only bind a body sent as application/json, and a
        // browser won't send that type to another site without asking it first (a CORS "preflight"),
        // which we never allow. So another website can't make a visitor's browser post this form.

        if (!string.IsNullOrEmpty(request.Website)) return Results.Ok();

        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true))
            return Results.ValidationProblem(errors
                .GroupBy(e => e.MemberNames.FirstOrDefault() ?? "")
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage ?? "Invalid.").ToArray()));

        // Unlike the order emails, this one has nowhere else to live: if it fails, the request is lost.
        // So the visitor is told, and can phone instead.
        if (!await notifier.ContactRequestAsync(request, ct))
            return Results.Problem("We couldn't send your request. Please call us instead.", statusCode: StatusCodes.Status503ServiceUnavailable);

        loggers.CreateLogger(typeof(ContactEndpoint)).LogInformation("Contact request received for {Make}", request.Make);
        return Results.Ok();
    }
}
