# AICloudProxy.com — marketing website

Static marketing site for **AI Cloud Proxy** (the app lives in `../src/AiCloudProxy`).

Stack: hand-written **HTML + CSS + vanilla JS** — no build step, deployable to any static host
(Netlify, Vercel, GitHub Pages, Cloudflare Pages, a simple web server…).

> **Both pages are self-contained.** The shared CSS and the small enhancement script are
> **inlined** into `index.html` and `privacy.html`, and the brand mark is an inline SVG.
> That means the layout, colours and typography can never break because of a failed
> `/assets` upload — the only files that must exist on the host are the two `.html` files.
> When you change the design, update the `SHARED STYLES` block in **both** files (keep them
> identical). Screenshots and the social image are still separate files that need uploading.

## Preview locally

```powershell
# From this web/ folder
python -m http.server 8080
# or
npx serve .
```

Then open <http://localhost:8080>.

## Structure

```
web/
├── index.html                 # single-page marketing site (SEO meta + shared CSS/JS inlined)
├── privacy.html               # privacy policy page (shared CSS/JS inlined too)
├── robots.txt
├── sitemap.xml                # points at https://aicloudproxy.com/ and /privacy.html
├── assets/
│   ├── logo.svg               # brand mark source (also used as the favicon)
│   ├── og-image.png           # Open Graph / Twitter card image (1200×630)
│   └── screenshots/           # app screenshots (see that folder's README)
```

## Before you publish

1. **Screenshots** — all added: `assets/screenshots/{main-window,test-tab,log-options,quick-tour,system-tray}.png`.
   These are **separate files**: upload the whole `assets/` folder to the web root (next to
   `index.html`). If a screenshot is missing on the host, the page shows a tidy
   “Screenshot coming soon” placeholder instead of a broken image — never a broken layout.
2. **Download URL** — the four Download buttons in `index.html` point at the GitHub
   latest-release asset:
   `https://github.com/local1907/AiCloudProxy/releases/latest/download/AICloudProxy.exe`.
   It always serves the newest uploaded `AICloudProxy.exe`, so no update is needed per release —
   only if the repo owner/name or the asset filename changes.
3. **Social image** — `index.html` references `assets/og-image.png` for Open Graph/Twitter.
   It is already generated from `scripts/og-card.html` (open that file at a 1200×630 viewport
   and save a screenshot as `assets/og-image.png`). Re-render it whenever the tagline or brand changes.
4. **Domain** — the `canonical`, Open Graph and `sitemap.xml` URLs assume
   `https://aicloudproxy.com/`. Update them if you deploy elsewhere.
5. After deploying, submit the site to Google Search Console and Bing Webmaster Tools.

## Troubleshooting: "the site looks unstyled / broken"

That symptom means the page loaded but its assets didn't. The pages are self-contained now, so
check the host instead:

1. `curl -I https://aicloudproxy.com/` — should be `200`.
2. If the text appears as a plain document with no dark theme, the uploaded `index.html` is an
   old copy (from when it linked `assets/styles.css`). Re-upload the current `index.html`.
3. Missing screenshots/social image → upload the `assets/` folder to the web root.
4. `403` on `/assets/` with `404` on its files means the folder exists on the host but is empty.

## SEO: sister-site cross-linking

The footer contains an **“Our Other Tools”** block that links to our sister project
**GigTaxHelper** (<https://www.gigtaxhelper.com/>) with descriptive anchor text and a short
summary. This is intentional internal-linking between our properties so each site passes
authority to the other. When GigTaxHelper's own site gets a comparable “Other tools” block
pointing back to AICloudProxy.com, both benefit. Keep the link text descriptive (not “click
here”) and update it if GigTaxHelper's description changes.
