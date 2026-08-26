# Brand Palette Guidelines — Dawn

Version: 1.0  
Status: Active  
Scope: All personal software products, OSS projects, GitHub assets, documentation, websites, and promotional materials

---

## 1. Brand Concept

The shared brand theme is **“Dawn / 夜明け”**.

The palette represents the moment when night begins to give way to morning:

- **Orange** — the rising sun and first strong light
- **Lavender** — the soft transition between night and morning
- **Blue** — the remaining pre-dawn sky
- **Near-black** — mountains, buildings, and the landscape seen in silhouette
- **Mist white** — haze, light, whitespace, and neutral backgrounds

The intended impression is:

- modern
- technical
- calm
- optimistic
- understated but memorable
- suitable for both enterprise utilities and open-source products

The goal is not to make every product icon identical.  
**Product identity should come from shape; family identity should come from color and visual tone.**

---

## 2. Core Palette

| Token | Name | Hex | RGB | Primary role |
|---|---|---:|---:|---|
| `--brand-orange` | Dawn Orange | `#FF6B1A` | 255, 107, 26 | Primary accent, sunrise, emphasis |
| `--brand-lavender` | Dawn Lavender | `#C6A7E8` | 198, 167, 232 | Soft secondary surface, transition color |
| `--brand-blue` | Pre-dawn Blue | `#4057D6` | 64, 87, 214 | Technical accent, trust, structure |
| `--brand-black` | Silhouette Black | `#191827` | 25, 24, 39 | Main dark tone, outlines, structure, foreground |
| `--brand-mist` | Morning Mist | `#F2EEF5` | 242, 238, 245 | Light background, whitespace, light surfaces |

---

## 3. Color Roles

### Dawn Orange — `#FF6B1A`

Use as the strongest accent color.

Recommended uses:

- one important tile or area in an icon
- active states
- key highlights
- important links or call-to-action accents
- small separators or emphasis lines
- sunrise elements in promotional artwork

Do not use it as the dominant full-screen background by default.

Orange should remain visually scarce so that it retains meaning and impact.

---

### Dawn Lavender — `#C6A7E8`

Use as the soft transition color.

Recommended uses:

- secondary icon tiles
- supporting surfaces
- subtle highlights
- soft gradients
- decorative backgrounds
- non-critical UI accents

Lavender should soften the contrast between the orange, blue, and dark tones.

---

### Pre-dawn Blue — `#4057D6`

Use as the primary technical color.

Recommended uses:

- secondary accent
- product UI elements
- informational states
- diagrams
- links
- one of the main icon regions
- technical illustrations

It should communicate reliability without making the entire brand look like a generic “blue tech company”.

---

### Silhouette Black — `#191827`

Use instead of pure black whenever possible.

Recommended uses:

- logo frames
- icon arches and structural elements
- headings
- dark backgrounds
- navigation
- silhouettes
- borders

This color intentionally contains a subtle blue-purple character so it harmonizes with the dawn palette.

Avoid using `#000000` unless technically necessary.

---

### Morning Mist — `#F2EEF5`

Use as the light neutral.

Recommended uses:

- documentation backgrounds
- website sections
- light-mode surfaces
- inactive icon regions
- cards
- whitespace-heavy layouts

Prefer this over pure white when a softer branded appearance is desired.

---

## 4. Recommended Visual Balance

For general brand materials, use approximately:

- **Silhouette Black:** 35–45%
- **Pre-dawn Blue:** 20–30%
- **Dawn Lavender:** 15–25%
- **Dawn Orange:** 8–15%
- **Morning Mist:** as required for spacing and readability

This is not a strict mathematical rule.

The important principle is:

> **Orange is the accent, not the base.**

---

## 5. Icon System

All product icons should follow these rules:

1. Use simple, recognizable geometry.
2. Prefer flat or nearly-flat rendering.
3. Avoid heavy gloss, bevels, realistic 3D effects, and excessive glow.
4. Use a maximum of 3–5 palette colors in one icon.
5. Keep Dawn Orange limited to one focal region where possible.
6. Use Silhouette Black for structural shapes, borders, frames, or bridges.
7. Use Blue and Lavender as the main product surfaces.
8. Ensure the icon remains recognizable at small sizes.
9. Product icons may use different shapes, but the color system should remain consistent.

Example family rule:

- Product A → different symbol, same dawn palette
- Product B → different symbol, same dawn palette
- Product C → different symbol, same dawn palette

---

## 6. Current M365 Drive Mounter Application

For **M365 Drive Mounter**, the current icon direction is:

- four rounded tiles
- a curved arch / bridge element
- top-left tile: Dawn Orange
- lower-left tile: Pre-dawn Blue
- remaining tiles: Dawn Lavender / Morning Mist variation
- arch and outer structure: Silhouette Black

The arch visually represents connection / mounting / bridging while remaining abstract enough to function as a product mark.

---

## 7. Gradients

Gradients are allowed in promotional materials, but should remain secondary to the core colors.

### Recommended Dawn Gradient

```css
linear-gradient(
  120deg,
  #191827 0%,
  #4057D6 38%,
  #C6A7E8 68%,
  #FF6B1A 100%
)
```

### Soft Dawn Background

```css
linear-gradient(
  135deg,
  #F2EEF5 0%,
  #C6A7E8 50%,
  #4057D6 100%
)
```

### Dark Hero Background

```css
linear-gradient(
  135deg,
  #191827 0%,
  #24213A 45%,
  #4057D6 100%
)
```

Do not rely on gradients for the master logo itself.  
The logo must also work in flat colors.

---

## 8. UI Tokens

Suggested CSS variables:

```css
:root {
  --brand-orange: #FF6B1A;
  --brand-lavender: #C6A7E8;
  --brand-blue: #4057D6;
  --brand-black: #191827;
  --brand-mist: #F2EEF5;
}
```

Suggested semantic mapping:

```css
:root {
  --color-primary: var(--brand-blue);
  --color-accent: var(--brand-orange);
  --color-secondary: var(--brand-lavender);
  --color-foreground: var(--brand-black);
  --color-background: var(--brand-mist);
}
```

---

## 9. Documentation and GitHub Usage

### README / Documentation

Preferred:

- background: Morning Mist or white
- headings: Silhouette Black
- links: Pre-dawn Blue
- highlight lines or small badges: Dawn Orange
- secondary badges: Dawn Lavender

### GitHub social previews

Preferred:

- dark dawn background
- strong Silhouette Black / navy base
- orange horizon or accent
- lavender transition
- blue technical elements

### Screenshots

Do not tint screenshots aggressively.

The brand palette should frame the product rather than distort the product UI.

---

## 10. Promotional Artwork

Promotional materials may use literal dawn scenery:

- sunrise horizon
- dark mountain silhouettes
- city silhouettes
- blue night sky
- lavender transition sky
- orange sunlight

However, product icons should remain abstract and simple.

Use literal dawn imagery mainly for:

- GitHub social preview
- landing page hero images
- launch announcements
- social posts
- presentation title slides

---

## 11. Avoid

Avoid the following:

- generic all-blue SaaS palettes
- neon rainbow color schemes
- bright orange as a full-page dominant color
- pure black where Silhouette Black works
- too many unrelated colors
- assigning a different color palette to every product
- excessive gradients inside small icons
- glossy skeuomorphic app icons
- copying Microsoft, Windows, OneDrive, or other third-party brand marks

---

## 12. Brand Principle

The brand system should follow this rule:

> **Same dawn, different silhouettes.**

Each product may have its own symbol and personality, but all products should feel like they belong to the same family through:

- the Dawn color palette
- restrained flat design
- strong dark structure
- limited orange accent
- calm blue/lavender atmosphere
- consistent promotional styling

---

## 13. Canonical Palette Summary

```text
Dawn Orange      #FF6B1A
Dawn Lavender    #C6A7E8
Pre-dawn Blue    #4057D6
Silhouette Black #191827
Morning Mist     #F2EEF5
```

These colors are the canonical shared brand palette unless this document is explicitly revised.
