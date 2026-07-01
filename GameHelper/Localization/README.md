# Overlay localization

The main overlay UI uses keyed JSON resources loaded by `OverlayLocalization`.

## Files

- `en-US.json` is the English baseline and fallback.
- `zh-TW.json` contains Traditional Chinese translations for the international client.
- Other Path of Exile 2 international client language files may contain partial translations;
  missing keys fall back to `en-US.json`.

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

## Plugin resources

Plugins own their translations. A plugin that opts into localization should keep files under:

- `Plugins/<PluginName>/Localization/en-US.json`
- `Plugins/<PluginName>/Localization/zh-TW.json`

Use `new PluginLocalization(this.DllDirectory)` from the plugin, and copy the plugin's
`Localization/*.json` files to the deployed plugin directory in that plugin's `.csproj`.
Do not put plugin UI keys in the main `GameHelper/Localization/*.json` files.
