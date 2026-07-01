namespace Autopot
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Numerics;
    using System.Threading.Tasks;
    using ClickableTransparentOverlay.Win32;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;
    using Nefarius.ViGEm.Client;
    using Nefarius.ViGEm.Client.Targets;
    using Nefarius.ViGEm.Client.Targets.Xbox360;
    using Newtonsoft.Json;

    public sealed class AutopotCore : PCore<AutopotSettings>
    {
        private readonly Stopwatch key1Cooldown = Stopwatch.StartNew();
        private readonly Stopwatch key2Cooldown = Stopwatch.StartNew();

        private VitalsSnapshot lastVitals;
        private string serviceStatus = "Stopped";
        private string statusDetail = string.Empty;
        private bool safetyLogoutTriggered;
        private readonly Stopwatch safetyLogoutCooldown = new();
        private bool safetyLogoutWaitingRecovery;

        private ViGEmClient? _vigemClient;
        private IXbox360Controller? _vigemController;
        private bool _vigemReady;
        private bool _vigemInitAttempted;
        private string _vigemStatus = string.Empty;
        private readonly object _vigemLock = new();

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");

        public override void OnDisable()
        {
            DisposeViGEm();
        }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(SettingPathname))
            {
                var content = File.ReadAllText(SettingPathname);
                Settings = JsonConvert.DeserializeObject<AutopotSettings>(content) ?? new AutopotSettings();
            }

            if (Settings.BypassControllerMode)
                InitializeViGEm();
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(SettingPathname, JsonConvert.SerializeObject(Settings, Formatting.Indented));
        }

        private void InitializeViGEm()
        {
            if (_vigemReady || _vigemInitAttempted) return;
            _vigemInitAttempted = true;
            try
            {
                _vigemClient = new ViGEmClient();
                _vigemController = _vigemClient.CreateXbox360Controller();
                _vigemController.Connect();
                _vigemReady = true;
                _vigemStatus = "Virtual controller connected";
            }
            catch (Exception ex)
            {
                DisposeViGEm();
                _vigemStatus = $"Failed: {ex.Message}";
            }
        }

        private void DisposeViGEm()
        {
            try { _vigemController?.Disconnect(); } catch { }
            try { _vigemClient?.Dispose(); } catch { }
            _vigemController = null;
            _vigemClient = null;
            _vigemReady = false;
            _vigemInitAttempted = false;
        }

        private bool PressViGEmButton(Xbox360Button button)
        {
            if (!_vigemReady || _vigemController == null) return false;
            try
            {
                lock (_vigemLock)
                {
                    _vigemController.SetButtonState(button, true);
                    _vigemController.SubmitReport();
                }
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    try
                    {
                        lock (_vigemLock)
                        {
                            _vigemController?.SetButtonState(button, false);
                            _vigemController?.SubmitReport();
                        }
                    }
                    catch { }
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override void DrawSettings()
        {
            RefreshVitals();
            ImGui.Columns(2, "autopot_settings", false);

            DrawVitalsColumn();
            ImGui.NextColumn();
            DrawConfigurationColumn();

            ImGui.Columns(1);
        }

        public override void DrawUI()
        {
            // Keep ViGEm in sync with the bypass checkbox without needing a restart.
            if (Settings.BypassControllerMode && !_vigemReady)
                InitializeViGEm();
            else if (!Settings.BypassControllerMode && _vigemReady)
                DisposeViGEm();

            RefreshVitals();
            UpdateServiceStatus();

            if (Settings.ShowVitalsOverlay && lastVitals.Valid)
                DrawVitalsOverlay();

            if (TrySafetyLogout())
                return;

            if (!Settings.EnableAutoPot)
                return;

            if (!CanExecute(out _))
                return;

            if (Core.GHSettings.EnableControllerMode && !Settings.BypassControllerMode)
                return;

            if (Core.States.InGameStateObject.GameUi.ChatParent.IsChatActive)
                return;

            if (!lastVitals.Valid)
                return;

            EvaluateTriggers();
        }

        private void DrawVitalsColumn()
        {
            ImGui.SeparatorText(this.PluginText.T("section.current_vitals", "Current Vitals"));
            DrawVitalBar(this.PluginText.T("vital.life", "Life"), lastVitals.HpPercent, new Vector4(0.85f, 0.15f, 0.15f, 1f));
            DrawVitalBar(this.PluginText.T("vital.energy_shield", "Energy Shield"), lastVitals.EsPercent, new Vector4(0.92f, 0.92f, 0.92f, 1f));
            DrawVitalBar(this.PluginText.T("vital.mana", "Mana"), lastVitals.MpPercent, new Vector4(0.2f, 0.45f, 0.95f, 1f));

            ImGui.Spacing();
            ImGui.SeparatorText(this.PluginText.T("section.vitals_overlay", "Vitals Overlay"));
            ImGui.Checkbox(this.PluginText.Label("settings.show_in_game", "Show in game", "AutopotShowInGame"), ref Settings.ShowVitalsOverlay);

            ImGui.Spacing();
            ImGui.SeparatorText(this.PluginText.T("section.input_device", "Input Device"));
            DrawDeviceStatus();

            ImGui.Spacing();
            ImGui.SeparatorText(this.PluginText.T("section.bound_keys", "Bound Keys"));
            ImGui.Text(this.PluginText.F("settings.key_1_value", "Key 1: {0}", Settings.Key1));
            ImGui.Text(this.PluginText.F("settings.key_2_value", "Key 2: {0}", Settings.Key2));
        }

        private void DrawConfigurationColumn()
        {
            ImGui.SeparatorText(this.PluginText.T("section.configuration", "Configuration"));
            ImGui.Checkbox(this.PluginText.Label("settings.enable_autopot", "Enable AutoPot", "AutopotEnableAutoPot"), ref Settings.EnableAutoPot);
            ImGui.SameLine();
            var statusColor = Settings.EnableAutoPot && serviceStatus == "Running"
                ? new Vector4(0.3f, 1f, 0.3f, 1f)
                : new Vector4(0.7f, 0.7f, 0.7f, 1f);
            ImGui.TextColored(statusColor,
                this.PluginText.F("status.service_status", "Service Status: {0}", this.LocalizeServiceStatus(serviceStatus)));
            if (!string.IsNullOrEmpty(statusDetail))
                ImGuiHelper.ToolTip(this.LocalizeReason(statusDetail));

            ImGui.Spacing();
            DrawLogicModeCombo();

            ImGui.Spacing();
            DrawThresholdSliders();

            ImGui.Spacing();
            ImGui.SeparatorText(this.PluginText.T("section.safety", "Safety (Auto-Logout)"));
            ImGui.TextDisabled(
                this.PluginText.T("settings.safety_description", "Returns to character select via connection drop."));
            this.DrawSafetyLogoutRow(ref Settings.HpDisconnectEnabled, ref Settings.HpDisconnectPercent, "##hpdc", "HP");
            this.DrawSafetyLogoutRow(ref Settings.EsDisconnectEnabled, ref Settings.EsDisconnectPercent, "##esdc", "ES");
            this.DrawSafetyLogoutRow(ref Settings.MpDisconnectEnabled, ref Settings.MpDisconnectPercent, "##mpdc", "MP");
            ImGui.SetNextItemWidth(220);
            ImGui.SliderInt(
                this.PluginText.Label("settings.cooldown_after_logout", "Cooldown after logout", "AutopotCooldownAfterLogout"),
                ref Settings.SafetyLogoutCooldownSeconds,
                0,
                600,
                Settings.SafetyLogoutCooldownSeconds <= 0
                    ? this.PluginText.T("common.off", "Off")
                    : this.PluginText.F("common.seconds_short", "{0} s", Settings.SafetyLogoutCooldownSeconds));
            ImGuiHelper.ToolTip(
                this.PluginText.T("settings.cooldown_after_logout.tooltip", "Prevents instant re-logout after reconnecting with low life/mana. 0 disables the timer."));
            ImGui.Checkbox(
                this.PluginText.Label("settings.rearm_after_recovery", "Re-arm only after vitals recover", "AutopotRearmAfterRecovery"),
                ref Settings.SafetyLogoutRequireRecovery);
            ImGuiHelper.ToolTip(
                this.PluginText.T("settings.rearm_after_recovery.tooltip", "After a logout, auto-logout stays disabled until each enabled vital is above its threshold again."));

            ImGui.Spacing();
            ImGui.SeparatorText(this.PluginText.T("section.hotkeys", "Hotkeys"));
            this.DrawHotkeyRow("key1", this.PluginText.T("settings.key1_label", "Key 1 (Life/Hybrid)"),
                ref Settings.Key1Enabled, ref Settings.Key1);
            this.DrawHotkeyRow("key2", this.PluginText.T("settings.key2_label", "Key 2 (Mana/Utility)"),
                ref Settings.Key2Enabled, ref Settings.Key2);

            ImGui.Spacing();
            ImGui.SeparatorText(this.PluginText.T("section.input_delays", "Input Delays"));
            ImGui.SetNextItemWidth(220);
            ImGui.SliderInt(this.PluginText.Label("settings.key1_delay", "Key1 Delay", "AutopotKey1Delay"), ref Settings.Key1DelayMs, 100, 10000,
                this.PluginText.F("common.milliseconds", "{0} ms", Settings.Key1DelayMs));
            ImGui.SetNextItemWidth(220);
            ImGui.SliderInt(this.PluginText.Label("settings.key2_delay", "Key2 Delay", "AutopotKey2Delay"), ref Settings.Key2DelayMs, 100, 10000,
                this.PluginText.F("common.milliseconds", "{0} ms", Settings.Key2DelayMs));

            ImGui.Spacing();
            ImGui.Checkbox(this.PluginText.Label("settings.run_in_hideout", "Run in hideout", "AutopotRunInHideout"), ref Settings.RunInHideout);
        }

        private void DrawLogicModeCombo()
        {
            if (ImGui.BeginCombo(this.PluginText.Label("settings.logic_mode", "Logic Mode", "AutopotLogicMode"), this.PluginText.T($"logic_mode.{Settings.LogicMode}", LogicModeLabels.Display(Settings.LogicMode))))
            {
                foreach (var mode in LogicModeLabels.All)
                {
                    bool selected = Settings.LogicMode == mode;
                    if (ImGui.Selectable(this.PluginText.T($"logic_mode.{mode}", LogicModeLabels.Display(mode)), selected))
                        Settings.LogicMode = mode;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        private void DrawThresholdSliders()
        {
            bool showHp = Settings.LogicMode is LogicMode.ManaAndLife or LogicMode.LifeOnly
                or LogicMode.EnergyShield or LogicMode.HybridLifeEs or LogicMode.ManaAndEs
                or LogicMode.LifeEsMana;
            bool showMp = Settings.LogicMode is LogicMode.ManaAndLife or LogicMode.ManaOnly
                or LogicMode.ManaAndEs or LogicMode.LifeEsMana;

            if (showHp)
            {
                string hpLabel = Settings.LogicMode switch
                {
                    LogicMode.EnergyShield => "ES",
                    LogicMode.ManaAndEs => "ES",
                    LogicMode.HybridLifeEs => "Life+ES",
                    _ => "HP",
                };
                ImGui.SetNextItemWidth(220);
                ImGui.SliderInt($"##hpthresh", ref Settings.HpThresholdPercent, 1, 99, $"{hpLabel} %d%%");
            }

            if (showMp)
            {
                ImGui.SetNextItemWidth(220);
                ImGui.SliderInt("##mpthresh", ref Settings.MpThresholdPercent, 1, 99, "MP %d%%");
            }
        }

        private void DrawSafetyLogoutRow(
            ref bool enabled,
            ref int thresholdPercent,
            string sliderId,
            string vitalLabel)
        {
            ImGui.Checkbox($"##en{sliderId}", ref enabled);
            ImGui.SameLine();
            ImGui.Text(this.PluginText.T("settings.logout_at", "Logout at"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt(sliderId, ref thresholdPercent, 1, 99, $"%d%% {vitalLabel}");
        }

        private void DrawHotkeyRow(string id, string label, ref bool enabled, ref VK key)
        {
            ImGui.Checkbox($"##en{id}", ref enabled);
            ImGui.SameLine();
            ImGui.Text(label);
            ImGui.SameLine();
            var tmp = key;
            if (ImGuiHelper.NonContinuousEnumComboBox($"##{id}", ref tmp))
                key = tmp;
        }

        private void DrawDeviceStatus()
        {
            bool vigem = InputDeviceStatus.IsViGEmBusInstalled();
            if (vigem)
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), this.PluginText.T("device.vigem_installed", "ViGEmBus: Installed"));
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), this.PluginText.T("device.vigem_not_installed", "ViGEmBus: Not installed"));
                ImGui.SameLine();
                if (ImGui.SmallButton(this.PluginText.Label("button.download", "[Download]", "AutopotDownloadVigem")))
                    System.Diagnostics.Process.Start(new ProcessStartInfo(InputDeviceStatus.ViGEmDownloadLink) { UseShellExecute = true });
            }

            if (InputDeviceStatus.IsControllerConnected())
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), this.PluginText.T("device.controller_detected", "Controller: Detected"));
            else
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), this.PluginText.T("device.controller_not_detected", "Controller: Not detected"));

            ImGui.Spacing();
            if (Core.GHSettings.EnableControllerMode)
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.2f, 1f), this.PluginText.T("device.poe_controller_mode", "PoE2 UI: Controller mode"));
            else
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), this.PluginText.T("device.poe_keyboard_mode", "PoE2 UI: Keyboard mode"));
            ImGuiHelper.ToolTip(this.PluginText.T("device.poe_controller_mode.tooltip", "In controller mode PoE2 ignores keyboard events — enable Controller bypass below."));

            ImGui.Spacing();
            ImGui.Checkbox(this.PluginText.Label("settings.controller_bypass", "Controller bypass (ViGEmBus)", "AutopotControllerBypass"), ref Settings.BypassControllerMode);
            ImGuiHelper.ToolTip(
                this.PluginText.T("settings.controller_bypass.tooltip", "Creates a virtual Xbox 360 controller via ViGEmBus and injects button presses\n" +
                "instead of keyboard events. Required when PoE2 is in controller mode.\n" +
                "ViGEmBus driver must be installed."));

            if (Settings.BypassControllerMode)
            {
                ImGui.Spacing();
                if (_vigemReady)
                    ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), this.PluginText.F("device.vigem_status", "ViGEm: {0}", this.LocalizeVigemStatus(_vigemStatus)));
                else
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                        this.PluginText.F("device.vigem_status", "ViGEm: {0}", _vigemStatus.Length > 0 ? this.LocalizeVigemStatus(_vigemStatus) : this.PluginText.T("status.initializing", "Initializing...")));

                ImGui.Spacing();
                ImGui.TextDisabled(this.PluginText.T("settings.hp_flask_button", "HP flask / Key1 button:"));
                DrawXboxButtonCombo("##ctrlbtn1", ref Settings.ControllerKey1Button);
                ImGui.TextDisabled(this.PluginText.T("settings.mp_flask_button", "MP flask / Key2 button:"));
                DrawXboxButtonCombo("##ctrlbtn2", ref Settings.ControllerKey2Button);
            }
        }

        private static void DrawXboxButtonCombo(string id, ref Xbox360Button current)
        {
            var allButtons = new[]
            {
                Xbox360Button.A, Xbox360Button.B, Xbox360Button.X, Xbox360Button.Y,
                Xbox360Button.LeftShoulder, Xbox360Button.RightShoulder,
                Xbox360Button.Up, Xbox360Button.Down, Xbox360Button.Left, Xbox360Button.Right,
                Xbox360Button.Start, Xbox360Button.Back,
                Xbox360Button.LeftThumb, Xbox360Button.RightThumb,
            };
            if (ImGui.BeginCombo(id, current.ToString()))
            {
                foreach (var btn in allButtons)
                {
                    bool sel = current == btn;
                    if (ImGui.Selectable(btn.ToString(), sel))
                        current = btn;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        private static void DrawVitalBar(string label, int percent, Vector4 fillColor)
        {
            ImGui.Text($"{label}");
            var avail = ImGui.GetContentRegionAvail().X;
            var barHeight = 18f;
            var pos = ImGui.GetCursorScreenPos();
            var drawList = ImGui.GetWindowDrawList();
            var bgMin = pos;
            var bgMax = new Vector2(pos.X + avail, pos.Y + barHeight);
            drawList.AddRectFilled(bgMin, bgMax, ImGuiHelper.Color(new Vector4(0.15f, 0.15f, 0.15f, 1f)), 3f);
            float fillW = avail * Math.Clamp(percent, 0, 100) / 100f;
            if (fillW > 0)
                drawList.AddRectFilled(bgMin, new Vector2(pos.X + fillW, pos.Y + barHeight), ImGuiHelper.Color(fillColor), 3f);
            var text = $"{percent}%";
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(new Vector2(pos.X + (avail - textSize.X) * 0.5f, pos.Y + (barHeight - textSize.Y) * 0.5f),
                ImGuiHelper.Color(new Vector4(1f, 1f, 1f, 1f)), text);
            ImGui.Dummy(new Vector2(avail, barHeight + 4f));
        }

        private void DrawVitalsOverlay()
        {
            ImGui.SetNextWindowBgAlpha(0.55f);
            ImGui.SetNextWindowSize(new Vector2(220, 0), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin(this.PluginText.Title("window.vitals", "AutoPot Vitals", "AutopotVitals"),
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.End();
                return;
            }

            DrawVitalBar(this.PluginText.T("vital.life", "Life"), lastVitals.HpPercent, new Vector4(0.85f, 0.15f, 0.15f, 1f));
            DrawVitalBar(this.PluginText.T("vital.es_short", "ES"), lastVitals.EsPercent, new Vector4(0.92f, 0.92f, 0.92f, 1f));
            DrawVitalBar(this.PluginText.T("vital.mana", "Mana"), lastVitals.MpPercent, new Vector4(0.2f, 0.45f, 0.95f, 1f));
            ImGui.End();
        }

        private void EvaluateTriggers()
        {
            bool key1Trigger = Settings.Key1Enabled && ShouldTriggerKey1(lastVitals);
            bool key2Trigger = Settings.Key2Enabled && ShouldTriggerKey2(lastVitals);

            if (key1Trigger && key1Cooldown.ElapsedMilliseconds >= Settings.Key1DelayMs)
            {
                bool sent = _vigemReady
                    ? PressViGEmButton(Settings.ControllerKey1Button)
                    : MiscHelper.KeyUp(Settings.Key1);
                if (sent) key1Cooldown.Restart();
            }

            if (key2Trigger && key2Cooldown.ElapsedMilliseconds >= Settings.Key2DelayMs)
            {
                bool sent = _vigemReady
                    ? PressViGEmButton(Settings.ControllerKey2Button)
                    : MiscHelper.KeyUp(Settings.Key2);
                if (sent) key2Cooldown.Restart();
            }
        }

        private bool ShouldTriggerKey1(VitalsSnapshot v) => Settings.LogicMode switch
        {
            LogicMode.ManaAndLife => v.HpPercent <= Settings.HpThresholdPercent,
            LogicMode.LifeOnly => v.HpPercent <= Settings.HpThresholdPercent,
            LogicMode.EnergyShield => v.EsPercent <= Settings.HpThresholdPercent,
            LogicMode.HybridLifeEs => v.HybridPercent <= Settings.HpThresholdPercent,
            LogicMode.ManaOnly => false,
            LogicMode.ManaAndEs => v.EsPercent <= Settings.HpThresholdPercent,
            LogicMode.LifeEsMana => v.HpPercent <= Settings.HpThresholdPercent || v.EsPercent <= Settings.HpThresholdPercent,
            _ => false,
        };

        private bool ShouldTriggerKey2(VitalsSnapshot v) => Settings.LogicMode switch
        {
            LogicMode.ManaAndLife => v.MpPercent <= Settings.MpThresholdPercent,
            LogicMode.LifeOnly => false,
            LogicMode.EnergyShield => false,
            LogicMode.HybridLifeEs => false,
            LogicMode.ManaOnly => v.MpPercent <= Settings.MpThresholdPercent,
            LogicMode.ManaAndEs => v.MpPercent <= Settings.MpThresholdPercent,
            LogicMode.LifeEsMana => v.MpPercent <= Settings.MpThresholdPercent,
            _ => false,
        };

        private bool TrySafetyLogout()
        {
            if (safetyLogoutTriggered)
                return true;

            if (!Settings.HpDisconnectEnabled && !Settings.EsDisconnectEnabled && !Settings.MpDisconnectEnabled)
                return false;

            if (!IsSafetyLogoutRearmed())
                return false;

            if (!CanSafetyLogout(out _))
                return false;

            if (!lastVitals.Valid)
                return false;

            if (Settings.HpDisconnectEnabled && lastVitals.HpPercent <= Settings.HpDisconnectPercent)
            {
                TriggerSafetyLogout("HP");
                return true;
            }

            if (Settings.EsDisconnectEnabled && lastVitals.HasEnergyShield
                && lastVitals.EsPercent <= Settings.EsDisconnectPercent)
            {
                TriggerSafetyLogout("ES");
                return true;
            }

            if (Settings.MpDisconnectEnabled && lastVitals.MpPercent <= Settings.MpDisconnectPercent)
            {
                TriggerSafetyLogout("MP");
                return true;
            }

            return false;
        }

        private void TriggerSafetyLogout(string vital)
        {
            safetyLogoutTriggered = true;
            safetyLogoutCooldown.Restart();
            safetyLogoutWaitingRecovery = Settings.SafetyLogoutRequireRecovery;
            MiscHelper.KillTCPConnectionForProcess(Core.Process.Pid);
            statusDetail = this.PluginText.F("status.auto_logout_triggered", "Auto-logout triggered ({0}).", vital);
        }

        private bool IsSafetyLogoutRearmed()
        {
            if (Settings.SafetyLogoutCooldownSeconds > 0 &&
                safetyLogoutCooldown.IsRunning &&
                safetyLogoutCooldown.Elapsed.TotalSeconds < Settings.SafetyLogoutCooldownSeconds)
            {
                return false;
            }

            if (safetyLogoutWaitingRecovery && !AreSafetyLogoutVitalsRecovered())
            {
                return false;
            }

            safetyLogoutWaitingRecovery = false;
            return true;
        }

        private bool AreSafetyLogoutVitalsRecovered()
        {
            if (!lastVitals.Valid)
            {
                return false;
            }

            if (Settings.HpDisconnectEnabled && lastVitals.HpPercent <= Settings.HpDisconnectPercent)
            {
                return false;
            }

            if (Settings.EsDisconnectEnabled && lastVitals.HasEnergyShield
                && lastVitals.EsPercent <= Settings.EsDisconnectPercent)
            {
                return false;
            }

            if (Settings.MpDisconnectEnabled && lastVitals.MpPercent <= Settings.MpDisconnectPercent)
            {
                return false;
            }

            return true;
        }

        private bool CanSafetyLogout(out string reason)
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                reason = $"Game state is {Core.States.GameCurrentState}, not InGameState.";
                safetyLogoutTriggered = false;
                return false;
            }

            var area = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;
            if (area.IsTown)
            {
                reason = "Player is in town.";
                return false;
            }

            if (!Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Life>(out var life))
            {
                reason = "Cannot read player Life component.";
                return false;
            }

            if (life.Health.Current <= 0)
            {
                reason = "Player is dead.";
                safetyLogoutTriggered = false;
                return false;
            }

            if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Buffs>(out var buffs)
                && buffs.StatusEffects.ContainsKey("grace_period"))
            {
                reason = "Player has grace period.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private void RefreshVitals()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                lastVitals = default;
                safetyLogoutTriggered = false;
                return;
            }

            if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Life>(out var life))
                lastVitals = VitalsSnapshot.FromLife(life);
            else
                lastVitals = default;
        }

        private void UpdateServiceStatus()
        {
            if (!Settings.EnableAutoPot)
            {
                serviceStatus = "Stopped";
                statusDetail = "Enable AutoPot to start monitoring.";
                return;
            }

            if (CanExecute(out var reason))
            {
                serviceStatus = "Running";
                statusDetail = "Monitoring vitals and pressing configured keys.";
            }
            else
            {
                serviceStatus = "Stopped";
                statusDetail = reason;
            }
        }

        private bool CanExecute(out string reason)
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                reason = $"Game state is {Core.States.GameCurrentState}, not InGameState.";
                return false;
            }

            if (!Core.Process.Foreground)
            {
                reason = "Game window is not in the foreground.";
                return false;
            }

            var area = Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails;
            if (area.IsTown)
            {
                reason = "Player is in town.";
                return false;
            }

            if (!Settings.RunInHideout && area.IsHideout)
            {
                reason = "Player is in hideout (enable 'Run in hideout' to allow).";
                return false;
            }

            if (!Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Life>(out var life))
            {
                reason = "Cannot read player Life component.";
                return false;
            }

            if (life.Health.Current <= 0)
            {
                reason = "Player is dead.";
                return false;
            }

            if (Core.States.InGameStateObject.CurrentAreaInstance.Player.TryGetComponent<Buffs>(out var buffs)
                && buffs.StatusEffects.ContainsKey("grace_period"))
            {
                reason = "Player has grace period.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private string LocalizeServiceStatus(string status) => status switch
        {
            "Running" => this.PluginText.T("status.running", "Running"),
            "Stopped" => this.PluginText.T("status.stopped", "Stopped"),
            _ => status,
        };

        private string LocalizeReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return reason;

            return reason switch
            {
                "Enable AutoPot to start monitoring." => this.PluginText.T("reason.enable_autopot", "Enable AutoPot to start monitoring."),
                "Monitoring vitals and pressing configured keys." => this.PluginText.T("reason.monitoring", "Monitoring vitals and pressing configured keys."),
                "Game window is not in the foreground." => this.PluginText.T("reason.game_not_foreground", "Game window is not in the foreground."),
                "Player is in town." => this.PluginText.T("reason.player_in_town", "Player is in town."),
                "Cannot read player Life component." => this.PluginText.T("reason.cannot_read_life", "Cannot read player Life component."),
                "Player is dead." => this.PluginText.T("reason.player_dead", "Player is dead."),
                "Player has grace period." => this.PluginText.T("reason.grace_period", "Player has grace period."),
                "Player is in hideout (enable 'Run in hideout' to allow)." =>
                    this.PluginText.T("reason.player_in_hideout", "Player is in hideout (enable 'Run in hideout' to allow)."),
                _ => reason,
            };
        }

        private string LocalizeVigemStatus(string status)
        {
            if (string.Equals(status, "Virtual controller connected", StringComparison.Ordinal))
            {
                return this.PluginText.T("status.virtual_controller_connected", "Virtual controller connected");
            }

            if (status.StartsWith("Failed: ", StringComparison.Ordinal))
            {
                return this.PluginText.F("status.failed", "Failed: {0}", status["Failed: ".Length..]);
            }

            return status;
        }
    }
}
