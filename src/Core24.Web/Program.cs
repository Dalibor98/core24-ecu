using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Core24.Web;
using Core24.Web.Data;
using Core24.Web.Features.Accounts;
using Core24.Web.Features.Contact;
using Core24.Web.Features.Downloads;
using Core24.Web.Features.Maintenance;
using Core24.Web.Features.Monitoring;
using Core24.Web.Features.Notifications;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Payments;
using Core24.Web.Features.Security;
using Core24.Web.Features.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.WebEncoders;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var env = builder.Environment;

// ---------- Logging (Serilog) ----------
// Everything that ILogger<T> writes goes to the console AND to a daily file in App_Data/logs
// (kept 30 days). Errors also go to the alert sink, which emails them (Features/Monitoring).
// Levels are set in the "Serilog" section of appsettings.json.
//
// The logger is built here and handed over as an instance. Serilog also has a global logger
// (Serilog.Log.Logger) that some of its parts use by default. We never touch it: the tests run
// several apps in one process, and with one global logger they wrote into each other's logs.
var alertSink = new ErrorAlertSink();
builder.Services.AddSingleton(alertSink);
var logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(env.ContentRootPath, config["Logs:Path"] ?? "App_Data/logs/core24-.log"),
        rollingInterval: RollingInterval.Day,   // core24-20261012.log, core24-20261013.log...
        retainedFileCountLimit: 30)
    .WriteTo.Sink(alertSink)
    .CreateLogger();
builder.Services.AddSerilog(logger, dispose: true); // dispose: flush and close the file when the app stops

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
builder.Services.AddOptions<MaintenanceOptions>().BindConfiguration(MaintenanceOptions.Section)
    .Validate(o => o.FileRetentionDays >= 1 && o.FirstReminderDays >= 1 && o.SecondReminderDays > o.FirstReminderDays, "Maintenance: retention must be at least 1 day, and the second reminder must come after the first.")
    .ValidateOnStart();
builder.Services.AddOptions<AlertOptions>().BindConfiguration(AlertOptions.Section)
    .Validate(o => o.To == "" || o.To.Contains('@'), "Alerts:To must be an email address (or empty to turn alerts off).")
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

// Two-factor login is optional (decision of 2026-10-12): any account, the admin too, can turn it on
// at /account/two-factor. Admin:RequireTwoFactor=true makes it REQUIRED for the admin pages: the
// policy then also needs the "amr=mfa" claim, which only a two-factor login adds.
var adminNeedsTwoFactor = config.GetValue("Admin:RequireTwoFactor", false);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Roles.Admin, p =>
    {
        p.RequireRole(Roles.Admin);
        if (adminNeedsTwoFactor) p.RequireClaim("amr", "mfa");
    });

// Every 30 minutes Identity re-checks each login cookie against the database (the "security stamp")
// and rebuilds the user's claims. Carry "amr" over, or an admin would lose access half an hour after logging in.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.OnRefreshingPrincipal = context =>
{
    var amr = context.CurrentPrincipal?.FindFirst("amr");
    if (amr is not null && context.NewPrincipal?.Identity is ClaimsIdentity identity && !identity.HasClaim("amr", amr.Value))
        identity.AddClaim(new Claim("amr", amr.Value));
    return Task.CompletedTask;
});

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
builder.Services.AddScoped<Maintenance>();

// Background jobs: they run for as long as the app runs (lesson 11).
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddHostedService<ErrorAlertService>();

// GET /health: "is the app alive, and can it reach its database and disk?" For uptime monitors.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database")
    .AddCheck<StorageHealthCheck>("storage");

// Behind a reverse proxy (Caddy, nginx) the app sees the proxy's IP and plain http. The proxy
// passes the real values in X-Forwarded-For / X-Forwarded-Proto; this tells the app to use them.
// Only switch it on when the app is reachable ONLY through the proxy (as in docker-compose.yml):
// otherwise anyone could send a fake X-Forwarded-For and dodge the rate limits.
var behindProxy = config.GetValue<bool>("ReverseProxy:Enabled");
if (behindProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // By default only a proxy on the same machine (127.0.0.1) is trusted. In Docker the proxy
        // is another container with its own IP, so trust whoever connects: only the proxy can.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

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
    o.Conventions.AuthorizePage("/Account/Password");
    o.Conventions.AuthorizePage("/Account/TwoFactor");
});

// Max 30 visits to the upload page per IP per 10 minutes: stops someone filling the disk with junk.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("uploads", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));

    // Max 10 requests per IP per 10 minutes for forms that send an email (forgot password, booking):
    // stops someone using us to flood a mailbox, and keeps us inside the email provider's limits.
    o.AddPolicy("emails", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
});

var app = builder.Build();

// ---------- Request pipeline (order matters: each step runs before the next) ----------
if (behindProxy) app.UseForwardedHeaders(); // first: everything after it must see the real IP and scheme

// One log line per request. The template uses the ROUTE ("/o/{token}"), not the path: the path
// contains secrets (order tokens, download tickets) and log files are not a safe place for them.
app.UseSerilogRequestLogging(o =>
{
    o.Logger = logger; // without this it writes to the global logger, which goes nowhere
    o.MessageTemplate = "HTTP {RequestMethod} {Route} responded {StatusCode} in {Elapsed:0} ms";
    o.EnrichDiagnosticContext = (diagnostics, http) =>
        diagnostics.Set("Route", http.GetEndpoint() is RouteEndpoint route
            ? "/" + route.RoutePattern.RawText?.TrimStart('/')   // Razor Pages give "o/{token}", minimal APIs "/health"
            : StaticOrUnknown(http.Request.Path));
    o.GetLevel = (http, _, ex) =>
        ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
        : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose  // uptime monitors call it every minute
        : LogEventLevel.Information;
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseSecurityHeaders(); // before static files, so the landing page gets them too
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
app.MapContactEndpoint();
app.MapHealthChecks("/health");

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

// Static files have no route. Log their path, but never one of ours that might hold a secret.
static string StaticOrUnknown(PathString path) =>
    path.StartsWithSegments("/o") || path.StartsWithSegments("/download") ? "(secret path)" : path.Value ?? "";

// Lets the test project start the app with WebApplicationFactory<Program>.
public partial class Program;
