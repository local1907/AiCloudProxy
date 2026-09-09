# AICloudProxy.com — marketing website

Static marketing site for **AI Cloud Proxy** (the app lives in `../src/AiCloudProxy`).

Stack: hand-written **HTML + CSS + vanilla JS** — no build step, deployable to any static host
(Netlify, Vercel, GitHub Pages, Cloudflare Pages, a simple web server…).

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
├── index.html                 # single-page marketing site (all SEO meta inline)
├── privacy.html               # privacy policy page (linked from the footer)
├── robots.txt
├── sitemap.xml                # points at https://aicloudproxy.com/ and /privacy.html
├── assets/
│   ├── styles.css
│   ├── app.js                 # mobile nav, scroll reveal, anchor offset
│   ├── logo.svg               # brand mark + favicon (echoes the app icon)
│   └── screenshots/           # drop app screenshots here (see that folder's README)
```

## Before you publish

1. **Screenshots** — all added: `assets/screenshots/{main-window,test-tab,log-options,quick-tour,system-tray}.png`.
2. **Download URL** — search `index.html` for `<!-- TODO` and replace the placeholder
   `href="#download"` buttons with your real release link (e.g. a GitHub Releases URL).
3. **Social image** — `index.html` references `assets/og-image.png` for Open Graph/Twitter.
   It is already generated from `scripts/og-card.html` (open that file at a 1200×630 viewport
   and save a screenshot as `assets/og-image.png`). Re-render it whenever the tagline or brand changes.
4. **Domain** — the `canonical`, Open Graph and `sitemap.xml` URLs assume
   `https://aicloudproxy.com/`. Update them if you deploy elsewhere.
5. After deploying, submit the site to Google Search Console and Bing Webmaster Tools.

## SEO: sister-site cross-linking

The footer contains an **“Our Other Tools”** block that links to our sister project
**GigTaxHelper** (<https://www.gigtaxhelper.com/>) with descriptive anchor text and a short
summary. This is intentional internal-linking between our properties so each site passes
authority to the other. When GigTaxHelper's own site gets a comparable “Other tools” block
pointing back to AICloudProxy.com, both benefit. Keep the link text descriptive (not “click
here”) and update it if GigTaxHelper's description changes.
