using System;
using System.Collections.Generic;
using System.Linq;
using GameHelper.RemoteEnums;
using GameHelper.RemoteObjects;
using GameHelper.RemoteObjects.Components;
using GameHelper.RemoteObjects.States.InGameStateObjects;

namespace AuraTracker.controllers;

internal static class MonsterFilters
{
    public static bool IsBeastMonster(Entity entity)
    {
        // A monster is a beast iff its MonsterVariety carries the game's "beast" category tag.
        // Core resolves this from the entity's metadata path against the shipped MonsterCategories
        // data table (the tag isn't reachable from live memory). See beast-detection memory.
        return entity != null && entity.MonsterCategory.HasFlag(MonsterCategory.Beast);
    }

    public static bool PassesAuraFilter(IReadOnlyList<BuffVisuals.BuffInfo> buffs, AuraTrackerSettings settings)
    {
        if (!settings.EnableAuraFilter)
        {
            return true;
        }

        var patterns = settings.AuraFilters
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        if (patterns.Count == 0)
        {
            return true;
        }

        bool Matches(string pattern) =>
            buffs.Any(b => !string.IsNullOrEmpty(b.Name) &&
                           b.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));

        return settings.AuraFilterMatchAll
            ? patterns.All(Matches)
            : patterns.Any(Matches);
    }
}
