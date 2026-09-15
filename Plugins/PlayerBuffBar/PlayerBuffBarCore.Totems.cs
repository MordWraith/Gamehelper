// <copyright file="PlayerBuffBarCore.Totems.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace PlayerBuffBar
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;
    using GameHelper;
    using GameHelper.RemoteObjects.Components;

    public sealed partial class PlayerBuffBarCore
    {
        private readonly List<BuffDisplayEntry> totemEntries = new();

        private void UpdateTotemEntries()
        {
            this.totemEntries.Clear();
            if (!this.Settings.BuffBars.Any(bar => bar.Enabled && bar.ShowTotems)) return;

            var area = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!area.Player.TryGetComponent<Actor>(out var actor, true)) return;
            var ownedIds = actor.DeployedEntityRecords.Select(record => (uint)record.EntityId).ToHashSet();
            var groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in area.AwakeEntities.Values)
            {
                if (!entity.IsValid || !ownedIds.Contains(entity.Id) ||
                    !entity.Path.StartsWith("Metadata/Monsters/Totems/", StringComparison.OrdinalIgnoreCase) ||
                    !entity.TryGetComponent<Life>(out var life, true) || !life.IsAlive)
                {
                    continue;
                }

                // @ suffixes identify variants/instances, not a separate totem skill.
                var name = entity.Path.Split('/').Last().Split('@')[0];
                var id = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
                groups[id] = groups.GetValueOrDefault(id) + 1;
            }

            foreach (var (id, count) in groups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                this.totemEntries.Add(new BuffDisplayEntry
                {
                    WatchId = id,
                    IsActive = true,
                    IsTotem = true,
                    Stacks = count,
                    Display = $"{PrettyName(id)} x{count}",
                });
            }

            if (this.Settings.AutoDownloadWikiIcons)
            {
                this.iconLoader.RequestDownloads(groups.Keys);
            }
        }
    }
}
