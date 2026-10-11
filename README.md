# CORE24

Truck ECU file service. A customer uploads an ECU file, the workshop modifies it, the customer pays, then downloads the result.

- **Landing page:** `src/Core24.Web/wwwroot/index.html` (the 3D scroll page)
- **Backend:** one ASP.NET Core app on .NET 10 (`src/Core24.Web`)
- **Tests:** `tests/Core24.Web.Tests`
- **Learning material:** `docs/`. Start with `docs/README.md`.

## Run it on your PC

Requirements: .NET 10 SDK. Nothing else: the database is a SQLite file, emails are written to a folder, and payments use a fake checkout page.

```powershell
cd C:\Users\Dalibor\source\repos\core24-ecu
dotnet tool restore                     # installs dotnet-ef (pinned in dotnet-tools.json)
dotnet run --project src/Core24.Web --launch-profile https
```

Open https://localhost:7086.

| What | Where |
|---|---|
| Landing page | `/` |
| Order form | `/order/new` |
| Admin (workshop) | `/admin`. Log in as `admin@core24.local` / `dev-admin-password` (from `appsettings.Development.json`) |
| Emails the app "sent" | `src/Core24.Web/App_Data/mail/*.eml` (double-click to open) |
| Database | `src/Core24.Web/App_Data/core24.db` (open with *DB Browser for SQLite*) |
| Uploaded files | `src/Core24.Web/App_Data/files/` |

The database is created and migrated on first start. To start from scratch, stop the app and delete `App_Data`.

## Test

```powershell
dotnet test
```

## Change the database schema

```powershell
# 1. change an entity class, then:
dotnet ef migrations add DescribeTheChange -p src/Core24.Web -o Data/Migrations
# 2. it is applied automatically on the next start (DbInitializer)
```

## Configuration

All settings are in `src/Core24.Web/appsettings.json`, and development overrides are in `appsettings.Development.json`. Real secrets (Stripe keys, SMTP password, admin password) never go into these files. Locally, use user secrets:

```powershell
cd src/Core24.Web
dotnet user-secrets init
dotnet user-secrets set "Payments:StripeSecretKey" "sk_test_..."
```

**Restart the app after changing settings.** Which email sender and payment provider are used is decided at startup. In Development, an orange banner shows when emails are only saved to `App_Data/mail`.

On a server, use environment variables (`Payments__StripeSecretKey=...`; the double underscore stands for `:`).

| Setting | Meaning |
|---|---|
| `App:PublicBaseUrl` | Address used in email links, e.g. `https://core24.eu` |
| `Uploads:*` | Max files, max size, allowed extensions, reject on checksum mismatch |
| `Email:Mode` | `Pickup` (folder, dev only) or `Smtp` |
| `Email:WorkshopAddress` | Where new orders go (the workshop) |
| `Payments:Provider` | `Dev` (fake checkout, dev only) or `Stripe` |
| `Admin:Email` / `Admin:Password` | Creates the workshop's admin login on startup if it doesn't exist |

## Try real Stripe payments locally (test mode)

1. Create a free Stripe account and stay in **test mode**.
2. Install the Stripe CLI, then run `stripe listen --forward-to https://localhost:7086/api/payments/stripe/webhook`. It prints a `whsec_...` secret.
3. Set user secrets: `Payments:Provider` = `Stripe`, `Payments:StripeSecretKey` = your `sk_test_...` key, `Payments:StripeWebhookSecret` = the `whsec_...` from step 2.
4. Pay with the test card `4242 4242 4242 4242`, any future date, any CVC.
