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
| Admin (workshop) | `/admin`. Log in as `admin@core24.local` / `dev-admin-password` (from `appsettings.Development.json`). Two-factor login is optional (each account can turn it on); `Admin:RequireTwoFactor=true` makes it required for the admin |
| Account settings | `/account/password` (change password), `/account/two-factor` (authenticator app), `/account/forgot-password` |
| Health check | `/health` answers `Healthy` when the database and file storage work |
| Logs | Console, plus one file per day in `src/Core24.Web/App_Data/logs/` (kept 30 days) |
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
| `Admin:RequireTwoFactor` | `false` (default): two-factor login is optional. `true`: admin pages need a two-factor login |
| `Alerts:To` | Who gets an email when the app logs an error (the developer). Empty: off |
| `Maintenance:FileRetentionDays` | Files of finished orders are deleted after this many days (default 90). Must match the privacy policy |
| `Maintenance:FirstReminderDays` / `SecondReminderDays` | Payment reminders for ready, unpaid orders (default 3 and 7 days) |
| `ReverseProxy:Enabled` | `true` only when the app is reachable **only** through a proxy (set by `docker-compose.yml`) |
| `Logs:Path` | Log file name pattern (default `App_Data/logs/core24-.log`) |

## Try real Stripe payments locally (test mode)

1. Create a free Stripe account and stay in **test mode**.
2. Install the Stripe CLI, then run `stripe listen --forward-to https://localhost:7086/api/payments/stripe/webhook`. It prints a `whsec_...` secret.
3. Set user secrets: `Payments:Provider` = `Stripe`, `Payments:StripeSecretKey` = your `sk_test_...` key, `Payments:StripeWebhookSecret` = the `whsec_...` from step 2.
4. Pay with the test card `4242 4242 4242 4242`, any future date, any CVC.

## Run in production (Docker)

Files: `Dockerfile` (the app), `docker-compose.yml` (app + Caddy for HTTPS), `Caddyfile`, `.env.example` (all settings).

```bash
# on the server (Linux, Docker installed, the domain's DNS pointing at it)
git clone https://github.com/Dalibor98/core24-ecu.git && cd core24-ecu
cp .env.example .env && nano .env     # fill in every value
docker compose up -d --build
docker compose logs -f app            # watch it start
```

Update to a new version: `git pull && docker compose up -d --build`. The data is in the `app-data` volume and survives this.

### Rehearse it on your PC first

Docker Desktop (on Windows it needs WSL: `wsl --install --no-distribution` in an admin PowerShell, then restart). Then:

```bash
docker compose -f docker-compose.yml -f docker-compose.local.yml up --build
```

The same image, volume and Caddy as production, but with the fake checkout and emails saved to `docker-mail/`, so no `.env` is needed. Open https://localhost and accept the certificate warning (Caddy made the certificate itself). Admin: `admin@core24.local` / `dev-admin-password`. Ctrl+C stops it; `docker compose -f docker-compose.yml -f docker-compose.local.yml down -v` also deletes its data.

## Production checklist

Before the first real customer:

- [ ] `.env` filled in: `App__PublicBaseUrl`, `AllowedHosts`, admin login, SMTP, Stripe **live** keys, `Alerts__To`
- [ ] Log in as admin and **change the admin password** to a long one only the workshop knows (two-factor login is optional, but recommended)
- [ ] Price list set in Admin → Price list (the seeded mods are placeholders)
- [ ] Email domain verified at the provider (SPF, DKIM, DMARC records), and a test email received
- [ ] Stripe webhook endpoint added (see `.env.example`), and a test event shows "200" in the Stripe dashboard
- [ ] One real order end to end with a small amount, then refunded from the admin page
- [ ] Booking form on the landing page sends an email to the workshop
- [ ] `/health` added to an uptime monitor (e.g. UptimeRobot, free)
- [ ] Daily backup of the `app-data` volume to another place, and **one restore tested**
- [ ] Terms and privacy policy online; the privacy policy states the file retention (`Maintenance:FileRetentionDays`)
