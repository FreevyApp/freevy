# Design system

polished-ui: v2
base: own tokens (no framework; Tailwind and Bootstrap deliberately not used)
motion layer: src/Freevy.App/ui/css/tokens.css (shared tokens), src/Freevy.App/ui/css/app.css (components)
pinned: 2026-10-20

## Notes
- The desktop UI is a web page hosted in WebView2 (src/Freevy.App/ui).
- Themes: a theme sets only primitives (neutral hue/chroma, accent L/C/H, lightness ramp for light themes) in css/themes.css; every color, ring, glow and shadow derives from them in css/tokens.css. Never hard-code a color in app.css. New theme = one block in themes.css plus one entry in THEMES in js/app.js.
- One accent per theme (Freevy's default is turquoise). No purple gradients, no emoji icons (inline SVG sprite, 1.75 stroke on a 24 grid), backdrop blur only on the title bar.
- Tab and view switches are frequent actions: 150ms fade only. Blur entrances are for single elements (editor card, dialogs, toasts).
