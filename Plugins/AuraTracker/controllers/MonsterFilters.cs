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
        if (entity == null)
        {
            return false;
        }

        string path = entity.Path ?? string.Empty;

        // Ignore legacy Bestiary metadata paths that do not apply to tame beasts.
        if (path.Contains("Bestiary", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Beast monsters (Spirit Walker / Tame Beast targets).
        if (path.Contains("/Beasts/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/Beast/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("WildBeast", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (entity.TryGetComponent<Stats>(out var stats, true) && stats != null)
        {
            if (HasPositiveStat(stats, GameStats.wild_beast_maximum_energy) ||
                HasPositiveStat(stats, GameStats.wild_beast_energy_per_hit) ||
                HasPositiveStat(stats, GameStats.wild_beast_damage_positive_percentage_final))
            {
                return true;
            }
        }

        if (entity.TryGetComponent<ObjectMagicProperties>(out var omp, true) && omp?.ModNames != null)
        {
            foreach (var mod in omp.ModNames)
            {
                if (string.IsNullOrEmpty(mod) ||
                    mod.Contains("bestiary", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (mod.Contains("beast", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
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

    private static bool HasPositiveStat(Stats stats, GameStats key)
    {
        if (stats.StatsChangedByItems != null &&
            stats.StatsChangedByItems.TryGetValue(key, out var itemsValue) &&
            itemsValue > 0)
        {
            return true;
        }

        if (stats.StatsChangedByBuffAndActions != null &&
            stats.StatsChangedByBuffAndActions.TryGetValue(key, out var buffValue) &&
            buffValue > 0)
        {
            return true;
        }

        return false;
    }
}
