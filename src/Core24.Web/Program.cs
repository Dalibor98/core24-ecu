using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Core24.Web;
using Core24.Web.Data;
using Core24.Web.Features.Accounts;
using Core24.Web.Features.Downloads;
using Core24.Web.Features.Notifications;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Payments;
using Core24.Web.Features.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.WebEncoders;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var env = builder.Environment;

// ---------- Settings ----------
// Each feature reads a typed options class. ValidateOnStart = a missing setting stops the app
// at startup with a clear message, instead of failing on the first customer.
builder.Services.AddOptions<AppOptions>().BindConfiguration(AppOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<UploadOptions>().BindConfiguration(UploadOptions.Section)
    .Validate(o => o.AllowedExtensions.Length > 0, "Uploads:AllowedExtensions must list at least one extension.")
    .ValidateOnStart();
builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section);
builder.Services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.Section)
    .Validate(o => o.FromAddress.Contains('@') && o.WorkshopAddress.Contains('@'), "Email:FromAddress and Email:WorkshopAddress are required.")
    .Validate(o => o.Mode != "Smtp" || o.SmtpHost != "", "Email:SmtpHost is required when Email:Mode is Smtp.")
    .ValidateOnStart();
builder.Services.AddOptions<PaymentOptions>().BindConfiguration(PaymentOptions.Section)
    .Validate(o => o.Provider != "Stripe" || (o.StripeSecretKey != "" && o.StripeWebhookSecret != ""), "Payments:StripeSecretKey and Payments:StripeWebhookSecret are required when Payments:Provider is Stripe.")
    .ValidateOnStart();

// ---------- Database ----------
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(SqliteConnectionString(config.GetConnectionString("Default"), env.ContentRootPath)));

// ---------- Accounts (ASP.NET Core Identity) ----------
builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedEmail = true;   // also what makes claiming guest orders safe
        // Length beats complexity rules: 10+ characters, no "must contain a symbol".
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireDigit = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/account/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/account/denied";
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.Admin, p => p.RequireRole(Roles.Admin));

// Keys that encrypt login cookies and download tickets. Stored on disk so they survive a restart;
// without this, every deploy would log everyone out and break every download link.
builder.Services.AddDataProtection()
    .SetApplicationName("Core24")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(env.ContentRootPath, "App_Data", "keys")));

// ---------- Our features ----------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFileStore, LocalFileStore>();
builder.Services.AddSingleton<DownloadLinks>();
builder.Services.AddScoped<Notifier>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<GuestOrderClaimer>();

// Pick the real or the development implementation from configuration.
var emailMode = config["Email:Mode"] ?? "Smtp";
var paymentProvider = config["Payments:Provider"] ?? "Stripe";
if (!env.IsDevelopment() && (emailMode == "Pickup" || paymentProvider == "Dev"))
    throw new InvalidOperationException("Email:Mode=Pickup and Payments:Provider=Dev are for development only.");

if (emailMode == "Pickup") builder.Services.AddScoped<IEmailSender, PickupFolderEmailSender>();
else builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();

if (paymentProvider == "Dev") builder.Services.AddSingleton<IPaymentGateway, DevPaymentGateway>();
else builder.Services.AddSingleton<IPaymentGateway, StripePaymentGateway>();

// ---------- Web ----------
// By default Razor HTML-encodes every non-ASCII character ("€" becomes "&#x20AC;"). Safe, but noisy;
// allowing all Unicode keeps "€", "č", "ž" readable while <, >, & and quotes are still encoded.
builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin", Roles.Admin);
    o.Conventions.AuthorizePage("/Account/Index");
});

// Max 30 visits to the upload page per IP per 10 minutes: stops someone filling the disk with junk.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("uploads", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));
});

var app = builder.Build();

// ---------- Request pipeline (order matters: each step runs before the next) ----------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseDefaultFiles();   // "/" → /index.html (the landing page)
app.UseStaticFiles();    // serves wwwroot: index.html, css
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication(); // who are you? (reads the login cookie)
app.UseAuthorization();  // are you allowed? (Admin pages, account page)

app.MapRazorPages();
app.MapStripeWebhook();
app.MapDownloads();
app.MapAccountEndpoints();

await DbInitializer.InitializeAsync(app.Services, config);
app.Run();

// SQLite resolves relative paths from the current folder, which differs between `dotnet run`,
// Visual Studio and tests. Anchor it to the project folder instead.
static string SqliteConnectionString(string? configured, string contentRoot)
{
    var csb = new SqliteConnectionStringBuilder(configured ?? "Data Source=App_Data/core24.db");
    if (!Path.IsPathRooted(csb.DataSource))
        csb.DataSource = Path.Combine(contentRoot, csb.DataSource);
    Directory.CreateDirectory(Path.GetDirectoryName(csb.DataSource)!);
    return csb.ToString();
}

// Lets the test project start the app with WebApplicationFactory<Program>.
public partial class Program;
