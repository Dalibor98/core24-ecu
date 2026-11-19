using System.Net;
using System.Text;
using Core24.Web.Data;
using Core24.Web.Features.Catalog;
using Core24.Web.Features.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Core24.Web.Tests.TestFlows;

namespace Core24.Web.Tests;

/// <summary>
/// The two kinds of order: an original file with a fixed price from the price list,
/// and a special request whose price the workshop sets after agreeing it by email.
/// </summary>
public class PriceListTests : IDisposable
{
    private readonly TestApp app = new();

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Original_file_order_takes_vehicle_and_price_from_the_price_list()
    {
        var response = await PostOrderAsync(app.Browser(), "driver@example.com",
            ("Input.Type", "OriginalFile"), ("Input.PriceListEntryId", "3"));
        AssertRedirect(response);

        var order = await LoadOrderAsync();
        Assert.Equal(OrderType.OriginalFile, order.Type);
        Assert.Equal("Scania", order.VehicleMake);
        Assert.Equal("DC13 Series G/P/R/T E5 · 12.7TDI 380-480hp · Motorola EMS S6/ACM DCU15", order.VehicleModel);
        Assert.Equal(30000, order.TotalCents);

        var item = Assert.Single(order.Items);
        Assert.Equal(3, item.PriceListEntryId);
        Assert.StartsWith("Original file: Scania", item.Name);
    }

    [Fact]
    public async Task Original_file_order_needs_a_line_that_is_still_offered()
    {
        var browser = app.Browser();

        // Nothing chosen.
        var response = await PostOrderAsync(browser, "driver@example.com", ("Input.Type", "OriginalFile"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Choose your vehicle and its computer.", await response.Content.ReadAsStringAsync());

        // A line the workshop stopped offering can't be ordered, even by posting its id directly.
        await app.WithDbAsync(db => db.PriceList.Where(p => p.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false)));
        response = await PostOrderAsync(browser, "driver@example.com", ("Input.Type", "OriginalFile"), ("Input.PriceListEntryId", "2"));
        Assert.Contains("no longer in our price list", await response.Content.ReadAsStringAsync());

        await app.WithDbAsync(async db => Assert.Equal(0, await db.Orders.CountAsync()));
    }

    [Fact]
    public async Task Special_request_has_no_price_until_the_workshop_sets_it()
    {
        // 1. The customer picks the vehicle from the list, describes the job; no price is shown or stored.
        var customer = app.Browser();
        var orderLink = AssertRedirect(await PostOrderAsync(customer, "driver@example.com",
            ("Input.Type", "SpecialRequest"), ("Input.PriceListEntryId", "1"), ("Input.Message", "Please check the ACM file too."))).Split('?')[0];

        var order = await LoadOrderAsync();
        Assert.Equal(OrderType.SpecialRequest, order.Type);
        Assert.Equal("Mercedes-Benz", order.VehicleMake);
        Assert.Equal(0, order.TotalCents);
        Assert.Empty(order.Items);

        var page = await customer.GetStringAsync(orderLink);
        Assert.Contains("email you the price", page);
        Assert.Contains("To be agreed", page);
        Assert.DoesNotContain("€0.00", page);

        var workshopMail = app.Emails().Single(e => e.To.ToString().Contains("workshop@core24.local"));
        Assert.StartsWith("Special request", workshopMail.Subject);
        Assert.Contains("To be agreed by email", workshopMail.HtmlBody);

        // 2. The workshop uploads the finished file, but can't ask for payment without a price.
        var admin = app.Browser();
        await LoginAsync(admin, AdminEmail, AdminPassword);
        var adminPage = $"/admin/orders/{order.Id}";
        var token = await admin.GetFormTokenAsync(adminPage);
        using (var upload = new MultipartFormDataContent())
        {
            upload.Add(new StringContent(token), "__RequestVerificationToken");
            upload.Add(FilePart(RandomBytes(2048), "done.bin"), "modifiedFiles", "done.bin");
            AssertRedirect(await admin.PostAsync($"{adminPage}?handler=Upload", upload));
        }
        AssertRedirect(await admin.PostFormAsync($"{adminPage}?handler=Ready", token));
        Assert.Equal(OrderStatus.InProgress, (await LoadOrderAsync()).Status);
        Assert.Contains("Set the price first", await admin.GetStringAsync(adminPage));

        // 3. Price agreed by email: the workshop types it in, then marks the order ready.
        AssertRedirect(await admin.PostFormAsync($"{adminPage}?handler=Price", token, ("name", "ECU + ACM work"), ("price", "450")));
        order = await LoadOrderAsync();
        Assert.Equal(45000, order.TotalCents);
        Assert.Equal("ECU + ACM work", Assert.Single(order.Items).Name);

        AssertRedirect(await admin.PostFormAsync($"{adminPage}?handler=Ready", token));
        Assert.Equal(OrderStatus.Ready, (await LoadOrderAsync()).Status);
        Assert.Contains("Pay €450.00", await customer.GetStringAsync(orderLink));

        // 4. Once the customer was asked to pay, the price is fixed.
        AssertRedirect(await admin.PostFormAsync($"{adminPage}?handler=Price", token, ("name", "Changed"), ("price", "1")));
        Assert.Equal(45000, (await LoadOrderAsync()).TotalCents);
    }

    [Fact]
    public async Task Special_request_for_a_vehicle_outside_the_list_is_typed_and_needs_a_description()
    {
        var browser = app.Browser();

        // No description: the workshop couldn't name a price.
        var response = await PostOrderAsync(browser, "driver@example.com",
            ("Input.Type", "SpecialRequest"), ("Input.VehicleNotListed", "true"), ("Input.Make", "Tatra"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Describe what you need", await response.Content.ReadAsStringAsync());

        // "Not in the list" without a make.
        response = await PostOrderAsync(browser, "driver@example.com",
            ("Input.Type", "SpecialRequest"), ("Input.VehicleNotListed", "true"), ("Input.Message", "Gearbox file"));
        Assert.Contains("Enter the vehicle", await response.Content.ReadAsStringAsync());

        AssertRedirect(await PostOrderAsync(browser, "driver@example.com",
            ("Input.Type", "SpecialRequest"), ("Input.VehicleNotListed", "true"), ("Input.Make", "Tatra"),
            ("Input.Model", "Phoenix 12.9 L, 2019"), ("Input.Message", "Gearbox file")));

        var order = await LoadOrderAsync();
        Assert.Equal("Tatra", order.VehicleMake);
        Assert.Equal("Phoenix 12.9 L, 2019", order.VehicleModel);
        Assert.Equal(0, order.TotalCents);
    }

    [Fact]
    public async Task Order_form_offers_only_lines_that_are_active()
    {
        await app.WithDbAsync(db => db.PriceList.Where(p => p.Id == 5).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false)));

        var page = await app.Browser().GetStringAsync("/order/new");
        Assert.Contains("data-brand=\"Scania\"", page);
        Assert.Contains("data-price=\"€370.00\"", page);
        Assert.DoesNotContain("data-brand=\"Volvo\"", page); // line 5, the only Volvo, is switched off
    }

    [Fact]
    public async Task Importing_a_csv_replaces_the_price_list_without_deleting_lines()
    {
        var csv = """
            brand;model;engine;ecu;price
            Scania;DC13 Series G/P/R/T E5;12.7TDI 380-480hp;Motorola EMS S7;345,50
            DAF;XF 106;12.9TDI MX13 430-530hp;DELPHI ETC3 Gen2/ACM CM1881;440
            """;

        var result = await ImportAsync(csv);
        Assert.True(result.Succeeded);
        Assert.Equal((1, 1, 4), (result.Added, result.Updated, result.Deactivated)); // DAF is new, Scania S7 changed price, the other 4 seeded lines are gone from the file

        await app.WithDbAsync(async db =>
        {
            Assert.Equal(6, await db.PriceList.CountAsync());                     // nothing deleted: old orders still point at their lines
            Assert.Equal(2, await db.PriceList.CountAsync(p => p.IsActive));
            Assert.Equal(34550, (await db.PriceList.SingleAsync(p => p.Ecu == "Motorola EMS S7")).PriceCents);
        });

        // One bad line: nothing at all is changed, and the message names the line.
        result = await ImportAsync("brand;model;engine;ecu;price\nMAN;TGX;12.4TDI;Bosch EDC17CV42;340\nMAN;TGS;10.5TDI;Bosch EDC7;abc");
        Assert.False(result.Succeeded);
        Assert.Contains("Line 3", Assert.Single(result.Errors));
        await app.WithDbAsync(async db => Assert.False(await db.PriceList.AnyAsync(p => p.Brand == "MAN")));

        // A file without the header line is refused.
        Assert.Contains("first line", Assert.Single((await ImportAsync("Scania;R;13L;EMS;300")).Errors));
    }

    [Fact]
    public async Task Workshop_imports_and_edits_the_price_list_in_the_admin()
    {
        var admin = app.Browser();
        await LoginAsync(admin, AdminEmail, AdminPassword);
        var token = await admin.GetFormTokenAsync("/admin/price-list");

        using (var upload = new MultipartFormDataContent())
        {
            upload.Add(new StringContent(token), "__RequestVerificationToken");
            // With the invisible "BOM" mark Excel puts at the start of a UTF-8 file.
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("brand;model;engine;ecu;price\r\nKubota;KX series;1.8TDI 35-48hp;Bosch EDC17CV45-C55;290\r\n")).ToArray();
            upload.Add(FilePart(bytes, "list.csv"), "file", "list.csv");
            AssertRedirect(await admin.PostAsync("/admin/price-list?handler=Import", upload));
        }
        var page = await admin.GetStringAsync("/admin/price-list?Q=kubota");
        Assert.Contains("Imported: 1 new, 0 changed, 5 no longer offered.", page);
        Assert.Contains("KX series", page);

        var id = 0;
        await app.WithDbAsync(async db => id = (await db.PriceList.SingleAsync(p => p.Brand == "Kubota")).Id);
        AssertRedirect(await admin.PostFormAsync($"/admin/price-list?handler=Save&id={id}", token, ("price", "310"), ("isActive", "true")));
        await app.WithDbAsync(async db => Assert.Equal(31000, (await db.PriceList.FindAsync(id))!.PriceCents));

        // Customers can't reach it.
        Assert.Equal(HttpStatusCode.Redirect, (await app.Browser().GetAsync("/admin/price-list")).StatusCode);
    }

    [Fact]
    public async Task Long_price_list_is_shown_in_pages_of_100()
    {
        var lines = Enumerable.Range(1, 250).Select(i => $"Scania;Model {i:000};12.7TDI;Bosch EDC{i};300");
        Assert.True((await ImportAsync("brand;model;engine;ecu;price\n" + string.Join('\n', lines))).Succeeded);

        var admin = app.Browser();
        await LoginAsync(admin, AdminEmail, AdminPassword);

        var first = await admin.GetStringAsync("/admin/price-list?Q=scania+model");
        Assert.Contains("250 line(s) found. Page 1 of 3.", first);
        Assert.Contains("Model 100<", first);
        Assert.DoesNotContain("Model 101<", first);

        var last = await admin.GetStringAsync("/admin/price-list?Q=scania+model&P=3");
        Assert.Contains("Model 250<", last);
        Assert.DoesNotContain("Next", last);

        // A page number that doesn't exist shows the nearest real page instead of an empty table.
        Assert.Contains("Page 3 of 3.", await admin.GetStringAsync("/admin/price-list?Q=scania+model&P=99"));
    }

    private async Task<PriceListImportResult> ImportAsync(string csv)
    {
        using var scope = app.Services.CreateScope();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return await scope.ServiceProvider.GetRequiredService<PriceListImport>().ImportAsync(stream);
    }

    private async Task<Order> LoadOrderAsync()
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders.AsNoTracking()
            .Include(o => o.Items).Include(o => o.Files)
            .OrderByDescending(o => o.CreatedAtUtc).FirstAsync();
    }
}
