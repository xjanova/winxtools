# WinXTools — go-live checklist (updater + selling on xman4289.com)

This documents everything wired up on 2026-07-17 and the **server steps you must run**
to make the updater work and the product sellable at ฿199.

## What is already done (code)

### WinXTools client (D:\Code\NetX) — committed
- License API now uses the real contract at `/api/v1/license/*` (commit `fab0d8b`).
- Updater now uses the **correct product slug `winx-tools`** and parses the real
  `VersionController` response (commit `184b1ac`).
- Pro price **฿199** shown in the upgrade banner / overlay / button (th + en).
- Purchase button already opens `https://xman4289.com/products/winx-tools`.

### xmanstudio (D:\Code\xmanstudio) — changes made (review + commit + deploy)
- `app/Http/Controllers/ProductController.php` — added `'winx-tools' => 'products.winxtools'`
  to `$customViews` (the DB slug is hyphenated; without this the custom landing page
  never rendered).
- `database/seeders/XmanProductsSeeder.php` — winx-tools `price` 990 → **199**.
- `resources/views/products/winxtools.blade.php` — rewritten: accurate features + the
  10 real screenshots + ฿199 pricing.
- `public_html/images/products/winxtools/*.png` — the 10 screenshots (also mirrored to `public/`).

## Server steps to go live (run on production)

1. **Deploy** the xmanstudio code above, then clear caches:
   ```bash
   php artisan db:seed --class=XmanProductsSeeder   # idempotent (updateOrCreate by slug) → sets price 199 + is_active
   php artisan view:clear && php artisan config:cache && php artisan route:cache
   ```
   > If the product must stay hidden until launch, set `is_active`/`is_coming_soon` in the admin
   > instead of re-seeding.

2. **Verify the page**: open `https://xman4289.com/products/winx-tools` — it must render the
   new landing page (not 404). If it 404s, the product row is `is_active=false`; activate it in
   `/admin/products` or via the seed above.

3. **Publish an app version** (this is why the updater currently says "No version available"):
   - Admin UI: `/admin/products/{winx-tools}/versions` → "สร้างเวอร์ชันใหม่":
     `version` (e.g. `0.1.0` — no leading `v`), `download_url` (public link to the built
     WinXTools zip/exe), `download_filename`, `changelog`. The newest active row becomes "latest".
   - OR GitHub auto-sync: set GitHub settings (`github_owner`, `github_repo`, `github_token`,
     `asset_pattern` e.g. `*.exe`) then Sync — it pulls `releases/latest` and matches the asset.
   - Verify: `GET https://xman4289.com/api/v1/products/winx-tools/version` returns the version JSON,
     and the app's "Check for updates" then detects it.

4. **Build & host the release binary**: `dotnet publish src/NetX.App -c Release` (self-contained
   single-file win-x64 per the csproj) → upload the zip → use that URL as `download_url`.
   Note: the desktop app downloads whatever `download_url` returns; a public direct link is
   simplest. (The auth-gated `download.product` web route requires a logged-in purchase.)

## Selling / licenses (how ฿199 becomes an activatable key)
- Product `winx-tools` at ฿199 → customer buys through the normal cart/checkout → on a
  completed order the platform issues a `LicenseKey` for the product → the customer enters that
  key in WinXTools → Settings → Activate. The client calls `/api/v1/license/activate` (already
  working) and unlocks Pro.
- A free trial already works: on first run the app calls `/api/v1/license/demo` and gets a
  short Pro trial.

## Notes / gaps
- The version API has **no sha256/hash** field — downloads are only TLS-verified. Consider
  adding a hash later for integrity.
- This dev machine currently has an active 3-day Pro demo (started while capturing screenshots) — harmless.
