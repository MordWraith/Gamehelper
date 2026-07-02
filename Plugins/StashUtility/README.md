# StashUtility

StashUtility is a plugin for **GameHelper2** designed to manage, filter, and highlight Waystones and Precursor/Breach Tablets for Path of Exile 2. It scans active stash tabs and your character's inventory, applying configurable criteria and mod matching to help you quickly identify run-ready items and avoid undesirable modifiers.

![Plugins UI Showcase](images/PluginsUI.jpg)
![Tablet Showcase](images/Tablet.jpg)
![Waystone Showcase](images/Waystone.jpg)

---

## Features

- **Waystone & Tablet Integration**: Automatically detects, parses, and highlights qualifying Waystones and Tablets inside active stash tabs (standard/quad/waystone layouts) and your character's inventory.
- **Granular Waystone Filter Criteria**:
  - **Min Tier**: Only highlight waystones equal to or above a specified tier (T1–T16).
  - **Filter Revives**: Limit highlights to waystones with at most $N$ max revives (e.g., filter out 0-revive maps).
  - **Item Rarity (%)**: Enforce a minimum item rarity percentage.
  - **Pack Size (%)**: Enforce a minimum monster pack size percentage.
  - **Monster Rarity (%)**: Filter by minimum rare monster count/rarity percentage.
  - **Monster Effectiveness (%)**: Filter by minimum monster effectiveness percentage.
  - **Waystone Drop Chance (%)**: Enforce a minimum waystone drop chance (includes quality calculations).
- **Tablet Mod Filter Manager**:
  - Full support for precursor and breach tablets.
  - Filters are categorized by mechanic type (Breach, Expedition, Delirium, Abyss, Incursion, Ritual, General).
  - Mark individual mods as **Good**, **Bad**, or **God** tier from the UI.
- **GREAT Highlight Conditions**:
  - Set specific thresholds for "GREAT" waystones (rarity, pack size, drop chance, etc.).
  - Set minimum good mods required for a tablet to be highlighted as Great.
  - Configure a minimum number of good mods to ignore bad mods (`MinGoodModsToIgnoreBad`).
- **Compile-Time Mod Database**:
  - Completely refactored to compile the mod definitions in C# (`Data/ModDatabase`).
  - No runtime JSON files required, minimizing dependencies.
- **Highly Customizable Visuals**:
  - Set custom border thickness and style (**Solid**, **Dashed**, or **Dotted**) for good vs. bad highlights.
  - Draw optional triangle corner indicators representing item rarity (Normal, Magic, Rare, Unique).
  - Draw a distinct, configurable **GREAT Arrow** (custom color, size, and position: Top-Left, Top-Right, Bottom-Left, Bottom-Right) on items that meet the Great thresholds.
  - Hides Normal (white) waystones completely if desired.
- **Developer Debug Tools**:
  - **UI Path Explorer**: Interactively click through indices in the game's UI hierarchy, highlight the active node or all child boundaries in-game, and resolve container offsets.
  - **Hovered Item Inspector**: Inspect the entity structure, raw game memory mods, components, and calculated stats of the hovered item.
  - **Memory Dumpers**: Write the resolved UI tree or full entity memory blocks directly to dump files.

---

## Installation

1. Download the latest release `StashUtility.zip` from the GitHub Releases page.
2. Extract the contents into the `Plugins` directory of your GameHelper2 directory:
   ```text
   GameHelper/
   └── Plugins/
       └── StashUtility/
           └── StashUtility.dll
   ```
3. Restart GameHelper2. The plugin will be recognized and loaded automatically.

---

## How to Use

1. **Enable Managers**: Open the GameHelper menu, locate the `StashUtility` section, and check **Enable Waystone Manager** and/or **Enable Tablet Manager**.
2. **Configure Thresholds**: Under **Filter Criteria** or **GREAT Highlight Conditions**, toggle and adjust sliders for minimum tiers, pack size, rarity, and drop chances.
3. **Set Up Mod Matching**:
   - **Waystones**: Search and add mods using the mod database dropdown, flagging them as Good or Bad.
   - **Tablets**: Navigate to the corresponding mechanics tab and check the appropriate Good/Bad/God options for the mods you want to target.
4. **Tune Visuals**: Choose highlight styles (Solid/Dashed/Dotted), select colors for good/bad/great states, and adjust border thickness.
5. **Calibrate UI Path (If Needed)**:
   - If the overlay borders do not align correctly with your stash slots, check the **UI Path Offsets** under debug settings.
   - By default, the path is set to `2,0,0,0,1,1,45,0,1`. You can customize the path indices to match UI changes.

---

## Building from Source

To compile the plugin yourself:

1. Place the `StashUtility` repository folder inside the `Plugins/` directory of the `GameHelper2` project.
2. Open the solution `GameOverlay.sln` in Visual Studio.
3. Select the `Release` build configuration and rebuild the solution.
4. The output will automatically copy `StashUtility.dll` and its assets to `GameHelper\bin\Release\net10.0-windows\win-x64\Plugins\StashUtility`.

---

## Release Workflow (GitHub CI/CD)

The repository uses GitHub Actions to automate releases. When you push a new tag matching `v*` (e.g., `v1.1.0`), the workflow:
1. Clones the parent `GameHelper2` repository.
2. Restores and builds the `StashUtility` plugin in Release mode using .NET 10.
3. Gathers the compiled `.dll` and `.pdb`.
4. Packages them into `StashUtility.zip` and uploads them to a newly created GitHub Release.

