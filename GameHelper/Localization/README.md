# Overlay localization

The main overlay UI uses keyed JSON resources loaded by `OverlayLocalization`.

## Files

- `en-US.json` is the English baseline and fallback.
- `zh-CN.json` contains Simplified Chinese translations.
- Every resource file must contain the same key set.

## Key naming

Use stable, lower-case dotted keys:

- `settings.<section>.<name>` for the main settings window.
- `<feature>.<name>` for non-settings overlay surfaces.
- Prefer descriptive names over matching the English text.

## ImGui labels

Do not use translated text as the only ImGui ID. Use the helpers:

- `OverlayLocalization.T(key, fallback)` for plain displayed text.
- `OverlayLocalization.F(key, fallback, args...)` for formatted text.
- `OverlayLocalization.Label(key, fallback, id)` for controls with hidden `##id`.
- `OverlayLocalization.Title(key, fallback, id)` for windows, tabs, and headers with `###id`.

Plugin UI should keep its current text until explicitly migrated.
