# HVO.WebSite.Themes - Shared UI Design System

Razor Class Library providing the **HVO Dark** design system, shared web assets, and reusable UI primitives for all HVOv9 Blazor applications.

## 📦 Package Information

- **Target Framework**: .NET 9.0
- **Type**: Razor Class Library (RCL)
- **Static Web Assets**: CSS themes, fonts, icons
- **Package Name**: `HVO.WebSite.Themes` (future NuGet)

## 🎯 Purpose

Centralized theme and design system for:
- **Consistent branding** across all HVO web properties
- **Dark-first astronomy UI** optimized for nighttime observatory use
- **Reusable UI components** (navigation badges, tabs, panels)
- **CSS custom properties** for easy theme customization
- **Web font hosting** (eliminates external CDN dependencies)

## 📁 Structure

```
HVO.WebSite.Themes/
├── wwwroot/
│   ├── css/
│   │   └── themes/
│   │       └── hvo-dark.css          # HVO Dark design system (1638 lines)
│   └── fonts/
│       └── [custom-fonts]            # Self-hosted web fonts
└── HVO.WebSite.Themes.csproj
```

## 🎨 HVO Dark Design System

### Color Palette (CSS Custom Properties)

#### Core Colors
```css
--hvo-body-bg: #05070d;              /* Deep space background */
--hvo-body-color: #f8fafc;           /* High-contrast text */
--hvo-accent: #3b82f6;               /* Blue accent (buttons, links) */
--hvo-accent-strong: #2563eb;        /* Darker blue (hover states) */
--hvo-accent-soft: rgba(59, 130, 246, 0.2);  /* Subtle highlights */
```

#### Semantic Colors
```css
--hvo-success-bg: rgba(34, 197, 94, 0.25);   /* Success states */
--hvo-success-fg: #bbf7d0;
--hvo-danger-bg: rgba(248, 113, 113, 0.25);  /* Error states */
--hvo-danger-fg: #fecaca;
--hvo-warning-bg: rgba(250, 204, 21, 0.25);  /* Warning states */
--hvo-warning-fg: #fef9c3;
--hvo-info-bg: rgba(56, 189, 248, 0.25);     /* Info states */
--hvo-info-fg: #e0f2fe;
```

#### Surfaces & Borders
```css
--hvo-card-bg: linear-gradient(145deg, rgba(15, 23, 42, 0.92), rgba(15, 23, 42, 0.65));
--hvo-card-glass: rgba(30, 41, 59, 0.6);
--hvo-panel-shadow: 0 12px 35px rgba(15, 23, 42, 0.45);
--hvo-border-muted: rgba(148, 163, 184, 0.18);
--hvo-border-strong: rgba(148, 163, 184, 0.2);
```

### Navigation Primitives

#### Badge Navigation
Compact pill-style navigation for primary menus:

```html
<nav class="nav-badge-bar">
    <a href="/observatory" class="nav-badge">
        <i class="bi bi-building nav-badge__icon"></i>
        Observatory
    </a>
    <a href="/weather" class="nav-badge nav-badge--active" aria-current="page">
        <i class="bi bi-cloud-sun nav-badge__icon"></i>
        Weather
    </a>
    <a href="/images" class="nav-badge">
        <i class="bi bi-camera nav-badge__icon"></i>
        Images
    </a>
</nav>
```

CSS Variables:
```css
--hvo-nav-badge-bg: rgba(148, 163, 184, 0.16);
--hvo-nav-badge-border: rgba(148, 163, 184, 0.38);
--hvo-nav-badge-padding-y: 0.28rem;
--hvo-nav-badge-padding-x: 0.62rem;
--hvo-nav-badge-font-size: 0.68rem;
```

#### Tab Row
Segmented control-style tabs for secondary navigation:

```html
<div class="hvo-tab-row">
    <a href="/current" class="hvo-tab-row__tab hvo-tab-row__tab--active" aria-current="page">
        Current
    </a>
    <a href="/history" class="hvo-tab-row__tab">
        History
    </a>
    <a href="/forecast" class="hvo-tab-row__tab">
        Forecast
    </a>
</div>
```

CSS Variables:
```css
--hvo-tab-bg: rgba(15, 23, 42, 0.7);
--hvo-tab-border: rgba(148, 163, 184, 0.35);
--hvo-tab-indicator: var(--hvo-accent);
```

## 🔧 Integration

### 1. Add Project Reference

```xml
<!-- In your Blazor app's .csproj -->
<ItemGroup>
  <ProjectReference Include="..\HVO.WebSite.Themes\HVO.WebSite.Themes.csproj" />
</ItemGroup>
```

### 2. Reference Theme Stylesheet

In your layout component (`MainLayout.razor` or `App.razor`):

```html
<head>
    <!-- Other head content -->
    <link rel="stylesheet" href="_content/HVO.WebSite.Themes/css/themes/hvo-dark.css" />
</head>
```

### 3. Enable Theme on Root Elements

Ensure `<html>` and `<body>` have `data-theme="hvo-dark"`:

```razor
<!DOCTYPE html>
<html lang="en" data-theme="hvo-dark">
<body data-theme="hvo-dark">
    @Body
</body>
</html>
```

Or in a Blazor component:
```razor
<HeadContent>
    <script>
        document.documentElement.setAttribute('data-theme', 'hvo-dark');
        document.body.setAttribute('data-theme', 'hvo-dark');
    </script>
</HeadContent>
```

## 🎓 Usage Examples

### Cards with Theme Colors

```razor
<div style="background: var(--hvo-card-bg); 
            border: 1px solid var(--hvo-border-muted);
            box-shadow: var(--hvo-panel-shadow);
            padding: 1.5rem;
            border-radius: 0.75rem;">
    <h3 style="color: var(--hvo-body-color);">Equipment Status</h3>
    <p style="color: var(--hvo-muted);">All systems nominal</p>
</div>
```

### Status Indicators

```razor
<!-- Success -->
<div style="background: var(--hvo-success-bg); 
            color: var(--hvo-success-fg);
            padding: 0.5rem 1rem;
            border-radius: 0.5rem;">
    ✓ Roof opened successfully
</div>

<!-- Danger -->
<div style="background: var(--hvo-danger-bg); 
            color: var(--hvo-danger-fg);
            padding: 0.5rem 1rem;
            border-radius: 0.5rem;">
    ⚠ Motor fault detected
</div>
```

### Bootstrap Override

The theme overrides Bootstrap's muted text color for better dark readability:

```css
/* In hvo-dark.css */
.text-muted {
    color: var(--hvo-muted) !important;
}
```

Use Bootstrap classes with automatic theme colors:
```html
<p class="text-muted">Timestamp: 2024-01-15 22:30:45</p>
```

## 🔧 Customization

### Adding New Theme Variables

Add new CSS custom properties at the top of `hvo-dark.css`:

```css
:root[data-theme="hvo-dark"] {
    /* Existing variables... */
    
    /* Your new variables */
    --hvo-chart-line: #60a5fa;
    --hvo-chart-grid: rgba(148, 163, 184, 0.1);
}
```

Then use in components:
```html
<canvas style="--line-color: var(--hvo-chart-line);"></canvas>
```

### Site-Specific Overrides

In consuming apps, create a site-specific CSS file loaded **after** the theme:

```html
<!-- In HVO.WebSite.v9 layout -->
<link rel="stylesheet" href="_content/HVO.WebSite.Themes/css/themes/hvo-dark.css" />
<link rel="stylesheet" href="css/site-overrides.css" />
```

`site-overrides.css`:
```css
:root[data-theme="hvo-dark"] {
    --hvo-accent: #8b5cf6;  /* Purple accent for this site */
}
```

## 📦 Adding New Assets

### Fonts
1. Place font files in `wwwroot/fonts/`
2. Add `@font-face` rules in `hvo-dark.css`:
```css
@font-face {
    font-family: 'CustomFont';
    src: url('../fonts/CustomFont-Regular.woff2') format('woff2');
    font-weight: normal;
}
```

### Icons
Use Bootstrap Icons (already integrated via CDN in consuming apps) or add custom icon fonts:
```html
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css">
```

## 🔗 Dependencies

- **None** - Pure CSS, no JavaScript required
- Bootstrap 5.3 (expected to be loaded by consuming app)
- Bootstrap Icons (expected to be loaded by consuming app)

## 📚 Used By

- `HVO.WebSite.v9` - Main observatory website
- `HVO.WebSite.Playground` - Development/testing site
- `HVO.RoofControllerV4.iPad` - iPad control app (future)
- `HVO.SkyMonitorV5.Viewer` - WASM sky monitor viewer (future)

## 🌙 Design Philosophy

### Dark-First for Astronomy
- **Preserve night vision** - Red tint or very low brightness for observatory use
- **High contrast** - Legible in total darkness
- **Muted backgrounds** - Reduce eye strain during long observing sessions

### CSS Custom Properties Over Sass
- **Runtime theming** - Change themes without rebuilding
- **Browser DevTools** - Live-edit colors in inspector
- **No build step** - Faster iteration for designers

### Minimal JavaScript
- **Pure CSS** - No theme-switcher JS required (single dark theme)
- **Static assets** - Fast CDN delivery
- **Accessible** - Works without JavaScript enabled

## 🔄 Future Enhancements

- [ ] Add HVO Light theme variant for daytime use
- [ ] Package as NuGet for external consumption
- [ ] Add more reusable UI components (buttons, forms, modals)
- [ ] Provide Figma/Sketch design files
- [ ] Add CSS utility classes (spacing, typography)
- [ ] Support theme switching with JavaScript
- [ ] Add SASS source files for advanced customization

## 📖 Related Documentation

- [Blazor Static Web Assets](https://learn.microsoft.com/en-us/aspnet/core/razor-pages/ui-class)
- [CSS Custom Properties](https://developer.mozilla.org/en-US/docs/Web/CSS/--*)
- [Bootstrap 5 Documentation](https://getbootstrap.com/docs/5.3/)
- [HVOv9 Blazor Component Best Practices](../../docs/guides/blazor-component-best-practices.md)
