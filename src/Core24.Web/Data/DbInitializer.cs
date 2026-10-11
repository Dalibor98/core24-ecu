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

        if (!await db.Mods.AnyAsync())
        {
            // PLACEHOLDERS taken from the site's services list. The workshop sets real names
            // and prices in Admin → Price list.
            db.Mods.AddRange(
                new Mod { Name = "Stage 1 performance remap", Description = "More torque for heavy loads and long climbs.", PriceCents = 29000, SortOrder = 10 },
                new Mod { Name = "Economy remap", Description = "Lower fuel use on long highway runs.", PriceCents = 24000, SortOrder = 20 });
            await db.SaveChangesAsync();
            log.LogInformation("Seeded placeholder price list");
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
