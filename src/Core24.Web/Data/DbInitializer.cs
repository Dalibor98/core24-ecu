using Core24.Web.Features.Catalog;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Data;

/// <summary>Runs once at startup: brings the database schema up to date and adds starting data.</summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DbInitializer));

        // Applies any migrations the database hasn't seen yet. Fine for one server;
        // with several servers you'd run migrations as a separate deploy step instead.
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        if (!await db.PriceList.AnyAsync())
        {
            // A FEW EXAMPLE LINES so the order form works on an empty database.
            // The workshop loads its real list in Admin → Price list → Import.
            db.PriceList.AddRange(
                new PriceListEntry { Brand = "Mercedes-Benz", Model = "Actros / Arocs / Axor", Engine = "12.8TDI 420-530hp OM471", Ecu = "Continental MCM/ACM/CPC", PriceCents = 37000 },
                new PriceListEntry { Brand = "Mercedes-Benz", Model = "Actros / Arocs / Axor", Engine = "12.8TDI 420-530hp OM460", Ecu = "Temic ZGS/ACM DCU15", PriceCents = 34000 },
                new PriceListEntry { Brand = "Scania", Model = "DC13 Series G/P/R/T E5", Engine = "12.7TDI 380-480hp", Ecu = "Motorola EMS S6/ACM DCU15", PriceCents = 30000 },
                new PriceListEntry { Brand = "Scania", Model = "DC13 Series G/P/R/T E5", Engine = "12.7TDI 380-480hp", Ecu = "Motorola EMS S7", PriceCents = 33000 },
                new PriceListEntry { Brand = "Volvo", Model = "D13 Euro-5", Engine = "12.8TDI 290-540hp", Ecu = "TRW EMS2.2/ACM", PriceCents = 34000 });
            await db.SaveChangesAsync();
            log.LogInformation("Seeded example price list lines");
        }

        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Roles.Admin))
            await roles.CreateAsync(new IdentityRole(Roles.Admin));

        // The workshop's login comes from configuration (user secrets / environment variables), never from code.
        var email = config["Admin:Email"];
        var password = config["Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        var users = sp.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByEmailAsync(email) is not null) return;

        var admin = new AppUser { UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(admin, password);
        if (!created.Succeeded)
        {
            log.LogError("Could not create admin {Email}: {Errors}", email, string.Join("; ", created.Errors.Select(e => e.Description)));
            return;
        }
        await users.AddToRoleAsync(admin, Roles.Admin);
        log.LogInformation("Created admin account {Email}", email);
    }
}
