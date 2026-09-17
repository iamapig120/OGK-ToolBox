# Design System

## Direction

Bright media workstation: cool daylight falling across a dense, carefully labeled editing desk. The product uses a restrained light canvas, white work surfaces, dark ink, and sharp ONGEKI-derived orange/cyan state colors. A quiet violet brand note anchors the product mark without competing with actions.

## Color Strategy

Restrained. Neutral surfaces carry the workspace; semantic accents stay below 10% of the visible area.

### Light tokens

- Canvas: `oklch(0.973 0.006 255)`
- Surface: `oklch(1 0 0)`
- Subtle surface: `oklch(0.948 0.009 255)`
- Ink: `oklch(0.245 0.025 255)`
- Muted ink: `oklch(0.47 0.025 255)`
- Border: `oklch(0.88 0.012 255)`
- Brand violet: `oklch(0.411 0.22 268)`
- Action orange: `oklch(0.62 0.20 35)`
- Information cyan: `oklch(0.59 0.12 220)`
- Success: `oklch(0.56 0.14 150)`
- Warning: `oklch(0.62 0.14 75)`
- Error: `oklch(0.56 0.19 20)`

### Dark tokens

- Canvas: `oklch(0.17 0.018 255)`
- Surface: `oklch(0.205 0.018 255)`
- Subtle surface: `oklch(0.24 0.018 255)`
- Ink: `oklch(0.94 0.008 255)`
- Muted ink: `oklch(0.73 0.018 255)`
- Border: `oklch(0.33 0.02 255)`

Electron CSS is the live token surface. Deprecated WPF resources still use precomputed sRGB equivalents; OKLCH remains the design source of truth.

## Typography

- Primary: Segoe UI Variable / Segoe UI
- Chinese fallback: Microsoft YaHei UI
- Japanese fallback: Yu Gothic UI
- Page title: 26px semibold
- Section title: 18px semibold
- Body: 14px regular
- Dense metadata: 12px regular or semibold

## Layout

- 228px collapsible primary navigation.
- 52px top command bar.
- Resource pages use filter/list/details structure; details are 360–420px when space permits.
- Dense lists use 52–60px rows and virtualization.
- At narrow widths, details become a full content pane rather than a modal.

## Components

- Buttons and fields use 8px radius; panels use 10px radius only when grouping is necessary.
- Selection uses a pale orange fill plus icon/text cue, not a saturated strip.
- Loading uses skeleton rows; empty states teach the next action.
- Transparent images render over a subtle checkerboard.
- Motion is 150–220ms and only communicates state.
