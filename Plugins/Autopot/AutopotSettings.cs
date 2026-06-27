using ClickableTransparentOverlay.Win32;
using GameHelper.Plugin;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Autopot
{
    public sealed class AutopotSettings : IPSettings
    {
        public bool EnableAutoPot = false;
        public LogicMode LogicMode = LogicMode.ManaAndLife;

        public int HpThresholdPercent = 80;
        public int MpThresholdPercent = 50;

        public bool HpDisconnectEnabled = false;
        public int HpDisconnectPercent = 10;
        public bool EsDisconnectEnabled = false;
        public int EsDisconnectPercent = 10;
        public bool MpDisconnectEnabled = false;
        public int MpDisconnectPercent = 10;

        /// <summary>Seconds after a safety logout before it can fire again (0 = no cooldown).</summary>
        public int SafetyLogoutCooldownSeconds = 120;

        /// <summary>Require vitals to rise above the logout threshold before re-arming.</summary>
        public bool SafetyLogoutRequireRecovery = true;

        public bool Key1Enabled = true;
        public VK Key1 = VK.KEY_1;
        public bool Key2Enabled = true;
        public VK Key2 = VK.KEY_2;

        public int Key1DelayMs = 2000;
        public int Key2DelayMs = 2000;

        public bool ShowVitalsOverlay = false;
        public bool RunInHideout = false;

        /// <summary>
        /// Use ViGEmBus virtual controller button injection instead of keyboard when a controller is detected.
        /// Requires ViGEmBus driver to be installed.
        /// </summary>
        public bool BypassControllerMode = false;

        /// <summary>Xbox 360 button pressed for Key1 (HP flask) in controller bypass mode.</summary>
        public Xbox360Button ControllerKey1Button = Xbox360Button.Left;

        /// <summary>Xbox 360 button pressed for Key2 (MP flask) in controller bypass mode.</summary>
        public Xbox360Button ControllerKey2Button = Xbox360Button.Right;
    }
}
