using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AuraTracker.controllers;
using AuraTracker.render;
using Coroutine;
using GameHelper;
using GameHelper.CoroutineEvents;
using GameHelper.Plugin;
using GameHelper.RemoteEnums;
using GameHelper.RemoteObjects.States;
using GameHelper.RemoteObjects.States.InGameStateObjects;
using Newtonsoft.Json;

namespace AuraTracker
{
    public sealed class AuraTracker : PCore<AuraTrackerSettings>
    {
        private const string PluginVersion = "1.0.1";

        private readonly DpsTracker dpsTracker = new();
        private readonly MonsterCollector monsterCollector = new();
        private readonly PanelRenderer panelRenderer = new();
        private readonly SettingsUiRenderer settingsRenderer = new(PluginVersion);
        private ActiveCoroutine? onAreaChange;
        private Vector2? defaultLargeMapCenter;

        private string SettingsPath => Path.Join(this.DllDirectory, "config", "AuraTracker.settings.json");

        public override void OnEnable(bool isGameOpened)
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var txt = File.ReadAllText(SettingsPath);
                    this.Settings = JsonConvert.DeserializeObject<AuraTrackerSettings>(txt) ?? new AuraTrackerSettings();
                }
                else
                {
                    this.Settings = new AuraTrackerSettings();
                }
            }
            catch
            {
                this.Settings = new AuraTrackerSettings();
            }

            this.onAreaChange = CoroutineHandler.Start(OnAreaChange(), string.Empty, 0);
            this.defaultLargeMapCenter = null;
        }

        public override void OnDisable()
        {
            this.onAreaChange?.Cancel();
            this.onAreaChange = null;
            this.dpsTracker.Reset();
            this.defaultLargeMapCenter = null;
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            this.settingsRenderer.Draw(this.Settings, this.PluginText);
        }

        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState && Core.States.GameCurrentState != GameStateTypes.EscapeState)
            {
                return;
            }

            InGameState inGame = Core.States.InGameStateObject;
            if (!this.Settings.DrawWhenGameInBackground && !Core.Process.Foreground)
            {
                return;
            }

            if (inGame.GameUi.SkillTreeNodesUiElements.Count > 0)
            {
                return;
            }

            if (this.IsMenuOpen(inGame))
            {
                return;
            }

            var overlaySize = new Vector2(Core.Overlay.Size.Width, Core.Overlay.Size.Height);
            var overlayCenter = overlaySize * 0.5f;

            List<MonsterCollector.MonsterSnapshot> monsters = this.monsterCollector.Collect(this.Settings, inGame, overlayCenter);
            if (monsters.Count == 0)
            {
                return;
            }

            this.panelRenderer.Render(monsters, this.Settings, this.dpsTracker, overlaySize);
        }

        private IEnumerator<Wait> OnAreaChange()
        {
            for (; ; )
            {
                yield return new Wait(RemoteEvents.AreaChanged);
                this.dpsTracker.Reset();
                this.defaultLargeMapCenter = null;
            }
        }

        private bool IsMenuOpen(InGameState inGame)
        {
            var largeMap = inGame.GameUi.LargeMap;
            var currentCenter = largeMap.Center;

            if (!this.defaultLargeMapCenter.HasValue)
            {
                this.defaultLargeMapCenter = currentCenter;
                return false;
            }

            const float menuThreshold = 0.00005f;
            var delta = Math.Abs(currentCenter.X - this.defaultLargeMapCenter.Value.X);

            if (delta >= menuThreshold)
            {
                return true;
            }

            this.defaultLargeMapCenter = currentCenter;
            return false;
        }
    }
}
