# Product Boundary

`src/OGKToolBox.Electron` is the only maintained and shipped desktop client. Implement all product UI and workflow changes there.

`src/OGKToolBox.App` is the deprecated WPF client. Keep it compile-compatible only. Do not add features, UI behavior, release work, or product fixes to WPF; it may be removed at any time.

# Electron UI

All Electron UI changes must follow the existing Electron design system and component patterns. Reuse established controls, visual states, spacing, colors, typography, and interaction behavior; do not introduce mismatched native controls or one-off UI styling.
