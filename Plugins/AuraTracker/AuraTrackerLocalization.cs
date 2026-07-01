namespace AuraTracker;

using GameHelper.Localization;

internal static class AuraTrackerLocalization
{
    internal static string[] RarityNames(PluginLocalization text) => new[]
    {
        text.T("rarity.normal", "Normal"),
        text.T("rarity.magic", "Magic"),
        text.T("rarity.rare", "Rare"),
        text.T("rarity.unique", "Unique"),
    };
}
