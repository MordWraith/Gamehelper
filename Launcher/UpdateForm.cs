namespace Launcher

{

    using System;

    using System.Collections.Generic;

    using System.Drawing;

    using System.Linq;

    using System.Threading;

    using System.Threading.Tasks;

    using System.Diagnostics;
    using System.Windows.Forms;
    using Shared.UpdateSecurity;



    internal enum UpdateFormPhase

    {

        Checking,

        Prompt,

        MigrationNotice,

        Downloading,

        ReadyToInstall,

        Error,

    }



    internal sealed class UpdateForm : Form

    {

        private static readonly Color Bg = Color.FromArgb(26, 28, 34);

        private static readonly Color PanelBg = Color.FromArgb(34, 36, 46);

        private static readonly Color Accent = Color.FromArgb(92, 140, 240);

        private static readonly Color TextMain = Color.FromArgb(235, 237, 245);

        private static readonly Color TextMuted = Color.FromArgb(165, 170, 185);

        private static readonly Color ChangelogBg = Color.FromArgb(24, 26, 32);

        private static readonly Color TabStripBg = Color.FromArgb(30, 32, 40);

        private static readonly Color ChangelogBorder = Color.FromArgb(48, 52, 66);



        private readonly string installDir;

        private readonly string appExePath;

        private readonly Label titleLabel;

        private readonly Label subtitleLabel;

        private readonly Panel contentPanel;

        private readonly Label statusLabel;

        private readonly ProgressBar progressBar;

        private readonly TabControl changelogTabs;

        private readonly TabPage tabCurrentRelease;

        private readonly TabPage tabAllReleases;

        private readonly RichTextBox currentReleaseBox;

        private readonly RichTextBox historyBox;

        private readonly RichTextBox errorBox;

        private IReadOnlyList<string> currentChangelogRaw = Array.Empty<string>();

        private readonly Button primaryButton;

        private readonly Button secondaryButton;

        private readonly Button languageButton;

        private readonly Button proxyButton;



        private UpdateOffer? currentOffer;

        private UpdateMigrationNotice.Info? currentMigration;

        private IReadOnlyList<ReleaseHistoryEntry> releaseHistory = Array.Empty<ReleaseHistoryEntry>();

        private CancellationTokenSource? downloadCts;

        private UpdateFormPhase phase = UpdateFormPhase.Checking;

        private UpdateProxyOptions proxyOptions = new();



        internal bool ShouldStartGame { get; private set; }



        internal UpdateForm(string installDir, string appExePath)

        {

            this.installDir = installDir;

            this.appExePath = appExePath;

            this.proxyOptions = UpdateProxyOptions.Load(installDir);

            UpdateService.ConfigureProxy(this.proxyOptions);



            this.Text = "GameHelper";

            this.StartPosition = FormStartPosition.CenterScreen;

            this.FormBorderStyle = FormBorderStyle.FixedDialog;

            this.MaximizeBox = false;

            this.MinimizeBox = false;

            this.ClientSize = new Size(640, 520);

            this.BackColor = Bg;

            this.ForeColor = TextMain;

            this.Font = new Font("Segoe UI", 10f);



            this.proxyButton = CreateButton(
                "Proxy",
                Color.FromArgb(55, 58, 72),
                new Point(424, 16),
                new Size(88, 28));

            this.proxyButton.Click += this.OnProxyClick;



            this.languageButton = CreateButton(

                LauncherLocalization.LanguageToggleLabel,

                Color.FromArgb(55, 58, 72),

                new Point(524, 16),

                new Size(92, 28));

            this.languageButton.Click += this.OnLanguageToggle;



            this.titleLabel = new Label

            {

                AutoSize = false,

                Location = new Point(24, 20),

                Size = new Size(488, 32),

                Font = new Font("Segoe UI", 14f, FontStyle.Bold),

                ForeColor = TextMain,

                Text = "GameHelper",

            };



            this.subtitleLabel = new Label

            {

                AutoSize = false,

                Location = new Point(24, 54),

                Size = new Size(592, 22),

                ForeColor = TextMuted,

                Text = LauncherLocalization.L("Checking for updates...", "Suche nach Updates..."),

            };



            this.contentPanel = new Panel

            {

                Location = new Point(24, 88),

                Size = new Size(592, 340),

                BackColor = PanelBg,

            };



            this.statusLabel = new Label

            {

                AutoSize = false,

                Location = new Point(16, 12),

                Size = new Size(560, 22),

                ForeColor = TextMain,

                Text = LauncherLocalization.L("Please wait...", "Bitte warten..."),

            };



            this.progressBar = new ProgressBar

            {

                Location = new Point(16, 44),

                Size = new Size(560, 24),

                Style = ProgressBarStyle.Continuous,

                Visible = false,

            };



            this.currentReleaseBox = new RichTextBox

            {

                Dock = DockStyle.Fill,

                BackColor = ChangelogBg,

                ForeColor = TextMain,

                BorderStyle = BorderStyle.None,

                ReadOnly = true,

                ScrollBars = RichTextBoxScrollBars.Vertical,

                WordWrap = true,

                Font = new Font("Segoe UI", 9.5f),

            };



            this.historyBox = new RichTextBox

            {

                Dock = DockStyle.Fill,

                BackColor = ChangelogBg,

                ForeColor = TextMain,

                BorderStyle = BorderStyle.None,

                ReadOnly = true,

                ScrollBars = RichTextBoxScrollBars.Vertical,

                WordWrap = true,

                Font = new Font("Segoe UI", 9.5f),

            };



            this.errorBox = new RichTextBox

            {

                Location = new Point(16, 40),

                Size = new Size(560, 280),

                Visible = false,

                ReadOnly = true,

                BorderStyle = BorderStyle.None,

                BackColor = ChangelogBg,

                ForeColor = TextMain,

                ScrollBars = RichTextBoxScrollBars.Vertical,

                WordWrap = true,

                Font = new Font("Segoe UI", 9.5f),

            };



            this.tabCurrentRelease = new TabPage();

            this.tabAllReleases = new TabPage();

            this.tabCurrentRelease.Controls.Add(this.currentReleaseBox);

            this.tabAllReleases.Controls.Add(this.historyBox);



            this.tabCurrentRelease.BackColor = ChangelogBg;

            this.tabCurrentRelease.UseVisualStyleBackColor = false;

            this.tabAllReleases.BackColor = ChangelogBg;

            this.tabAllReleases.UseVisualStyleBackColor = false;

            this.changelogTabs = new TabControl

            {

                Location = new Point(12, 38),

                Size = new Size(568, 290),

                Visible = false,

                DrawMode = TabDrawMode.OwnerDrawFixed,

                Appearance = TabAppearance.FlatButtons,

                SizeMode = TabSizeMode.Fixed,

                ItemSize = new Size(148, 30),

                Padding = new Point(0, 0),

                BackColor = PanelBg,

            };

            this.changelogTabs.TabPages.Add(this.tabCurrentRelease);

            this.changelogTabs.TabPages.Add(this.tabAllReleases);

            this.changelogTabs.DrawItem += this.OnChangelogTabDrawItem;

            this.changelogTabs.Paint += this.OnChangelogTabsPaint;

            this.tabCurrentRelease.Paint += this.OnChangelogTabPagePaint;

            this.tabAllReleases.Paint += this.OnChangelogTabPagePaint;

            this.changelogTabs.SelectedIndexChanged += (_, _) => this.changelogTabs.Invalidate();

            this.UpdateTabTitles();



            this.contentPanel.Controls.Add(this.statusLabel);

            this.contentPanel.Controls.Add(this.progressBar);

            this.contentPanel.Controls.Add(this.errorBox);

            this.contentPanel.Controls.Add(this.changelogTabs);



            this.primaryButton = CreateButton("OK", Accent, new Point(416, 448), new Size(200, 40));

            this.secondaryButton = CreateButton(

                LauncherLocalization.L("Cancel", "Abbrechen"),

                Color.FromArgb(55, 58, 72),

                new Point(24, 448),

                new Size(200, 40));

            this.primaryButton.Enabled = false;

            this.secondaryButton.Enabled = false;

            this.primaryButton.Click += this.OnPrimaryClick;

            this.secondaryButton.Click += this.OnSecondaryClick;



            this.Controls.Add(this.languageButton);
            this.Controls.Add(this.proxyButton);

            this.Controls.Add(this.titleLabel);

            this.Controls.Add(this.subtitleLabel);

            this.Controls.Add(this.contentPanel);

            this.Controls.Add(this.primaryButton);

            this.Controls.Add(this.secondaryButton);



            this.Load += this.OnFormLoad;

            this.FormClosing += this.OnFormClosing;

        }



        private static Button CreateButton(string text, Color back, Point location, Size size)

        {

            var button = new Button

            {

                Text = text,

                Location = location,

                Size = size,

                FlatStyle = FlatStyle.Flat,

                BackColor = back,

                ForeColor = Color.White,

                Font = new Font("Segoe UI", 10f, FontStyle.Bold),

                Cursor = Cursors.Hand,

            };

            button.FlatAppearance.BorderSize = 0;

            return button;

        }



        private void OnChangelogTabDrawItem(object? sender, DrawItemEventArgs e)

        {

            var selected = this.changelogTabs.SelectedIndex == e.Index;

            var back = selected ? Color.FromArgb(48, 52, 66) : TabStripBg;

            var fore = selected ? TextMain : TextMuted;

            using (var bgBrush = new SolidBrush(back))

            {

                e.Graphics.FillRectangle(bgBrush, e.Bounds);

            }

            if (selected)

            {

                using var accentPen = new Pen(Accent, 2);

                e.Graphics.DrawLine(

                    accentPen,

                    e.Bounds.Left + 2,

                    e.Bounds.Bottom - 1,

                    e.Bounds.Right - 2,

                    e.Bounds.Bottom - 1);

            }

            var text = this.changelogTabs.TabPages[e.Index].Text;

            TextRenderer.DrawText(

                e.Graphics,

                text,

                this.Font,

                e.Bounds,

                fore,

                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        }



        private void OnChangelogTabsPaint(object? sender, PaintEventArgs e)

        {

            var tabs = (TabControl)sender!;

            if (tabs.TabCount == 0)

            {

                return;

            }



            using (var stripBrush = new SolidBrush(TabStripBg))

            {

                var firstTab = tabs.GetTabRect(0);

                var lastTab = tabs.GetTabRect(tabs.TabCount - 1);

                e.Graphics.FillRectangle(stripBrush, 0, 0, tabs.Width, firstTab.Bottom);

                if (lastTab.Right < tabs.Width)

                {

                    e.Graphics.FillRectangle(

                        stripBrush,

                        lastTab.Right,

                        0,

                        tabs.Width - lastTab.Right,

                        firstTab.Height);

                }

            }



            var display = tabs.DisplayRectangle;

            var outer = new Rectangle(

                display.X - 2,

                display.Y - 2,

                display.Width + 3,

                display.Height + 3);

            using (var fillBrush = new SolidBrush(ChangelogBg))

            {

                e.Graphics.FillRectangle(fillBrush, outer);

            }



            using (var borderPen = new Pen(ChangelogBorder, 1))

            {

                e.Graphics.DrawRectangle(

                    borderPen,

                    display.X - 1,

                    display.Y - 1,

                    display.Width + 1,

                    display.Height + 1);

            }

        }



        private void OnChangelogTabPagePaint(object? sender, PaintEventArgs e)

        {

            var page = (TabPage)sender!;

            using var brush = new SolidBrush(ChangelogBg);

            e.Graphics.FillRectangle(brush, page.ClientRectangle);

        }



        private void OnLanguageToggle(object? sender, EventArgs e)

        {

            LauncherLocalization.ToggleLanguage();

            this.languageButton.Text = LauncherLocalization.LanguageToggleLabel;

            this.RefreshLocalizedUi();

        }



        private async void OnProxyClick(object? sender, EventArgs e)

        {

            if (this.phase is UpdateFormPhase.Checking or UpdateFormPhase.Downloading)
            {
                return;
            }

            using var form = new ProxySettingsForm(this.proxyOptions);
            if (form.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            this.proxyOptions = form.Options;
            try
            {
                this.proxyOptions.Save(this.installDir);
                UpdateService.ConfigureProxy(this.proxyOptions);
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"Proxy config save: {ex}");
                MessageBox.Show(this, ex.Message, "GameHelper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (this.phase == UpdateFormPhase.Error)
            {
                await this.RunUpdateFlowAsync();
            }

        }



        private void RefreshLocalizedUi()

        {

            this.UpdateTabTitles();

            this.proxyButton.Text = "Proxy";



            switch (this.phase)

            {

                case UpdateFormPhase.Checking:

                    this.ShowChecking();

                    break;

                case UpdateFormPhase.Prompt when this.currentOffer != null:

                    this.ShowPrompt(this.currentOffer);

                    break;



                case UpdateFormPhase.MigrationNotice when this.currentMigration != null:

                    this.ShowMigrationNotice(this.currentMigration);

                    break;

                case UpdateFormPhase.Downloading:

                    this.titleLabel.Text = LauncherLocalization.L("Downloading update", "Update wird heruntergeladen");

                    this.statusLabel.Text = LauncherLocalization.L("Download in progress...", "Download laeuft...");

                    break;

                case UpdateFormPhase.ReadyToInstall when this.currentOffer != null:

                    this.ShowReadyToRestart(this.currentOffer);

                    break;

                case UpdateFormPhase.Error:

                    this.primaryButton.Text = LauncherLocalization.L("Start without update", "Ohne Update starten");

                    this.secondaryButton.Text = LauncherLocalization.L("Exit", "Beenden");

                    break;

            }



            if (this.changelogTabs.Visible && this.currentOffer != null)
            {
                this.FillChangelogList(this.currentOffer.Changelog);
            }

            this.FillHistoryBox();

        }



        private void UpdateTabTitles()

        {

            this.tabCurrentRelease.Text = LauncherLocalization.L("This update", "Dieses Update");

            this.tabAllReleases.Text = LauncherLocalization.L("All releases", "Alle Releases");

        }



        private async void OnFormLoad(object? sender, EventArgs e)

        {

            await this.RunUpdateFlowAsync();

        }



        private void OnFormClosing(object? sender, FormClosingEventArgs e)

        {

            this.downloadCts?.Cancel();

        }



        private async Task RunUpdateFlowAsync()

        {

            try

            {

                this.currentOffer = null;
                this.currentMigration = null;

                var current = UpdateService.GetCurrentVersion(this.appExePath);

                this.subtitleLabel.Text = LauncherLocalization.L(

                    $"Installed: v{current}",

                    $"Installiert: v{current}");

                this.ShowChecking();



                var historyTask = ChangelogHistoryService.LoadMergedAsync(this.installDir);

                var checkResult = await UpdateService.CheckForUpdateAsync(this.appExePath, this.installDir);

                this.releaseHistory = await historyTask;

                this.FillHistoryBox();

                if (!string.IsNullOrWhiteSpace(checkResult.ErrorMessage))
                {
                    this.ShowError(checkResult.ErrorMessage);
                    return;
                }



                if (checkResult.Offer == null)

                {

                    if (checkResult.MigrationNotice != null &&
                        UpdateMigrationNotice.ShouldShow(this.installDir, checkResult.MigrationNotice, current))

                    {

                        this.currentMigration = checkResult.MigrationNotice;

                        this.ShowMigrationNotice(checkResult.MigrationNotice);

                        return;

                    }

                    this.ShouldStartGame = true;

                    this.Close();

                    return;

                }



                this.currentOffer = checkResult.Offer;

                this.ShowPrompt(checkResult.Offer);

            }

            catch (Exception ex)

            {

                LauncherLog.Write($"Update-Flow: {ex}");

                this.ShowError(UpdateErrors.Format(ex));

            }

        }



        private void HideErrorDetails()

        {

            this.errorBox.Visible = false;

            this.errorBox.Clear();

        }



        private void ShowChecking()

        {

            this.phase = UpdateFormPhase.Checking;

            this.HideErrorDetails();

            this.titleLabel.Text = "GameHelper";

            this.statusLabel.Text = LauncherLocalization.L(

                "Checking for updates on GitHub...",

                "Suche nach Updates auf GitHub...");

            this.progressBar.Visible = false;

            this.changelogTabs.Visible = false;

            this.primaryButton.Enabled = false;

            this.secondaryButton.Enabled = false;

            this.proxyButton.Enabled = false;

        }



        private IReadOnlyList<string> BuildChangelogLines(UpdateOffer offer)
        {
            if (offer.MigrationNotice == null)
            {
                return offer.Changelog;
            }

            var warning = LauncherLocalization.L(
                offer.MigrationNotice.MessageEn,
                offer.MigrationNotice.MessageDe);
            var maxAuto = offer.MigrationNotice.MaxAutoUpdateVersion;
            var header = string.IsNullOrWhiteSpace(maxAuto)
                ? LauncherLocalization.L(
                    $"IMPORTANT: v{offer.MigrationNotice.ManualInstallVersion} must be installed manually after this update.",
                    $"WICHTIG: v{offer.MigrationNotice.ManualInstallVersion} muss nach diesem Update manuell installiert werden.")
                : LauncherLocalization.L(
                    $"IMPORTANT: Auto-update stops at v{maxAuto}. From v{offer.MigrationNotice.ManualInstallVersion} install manually.",
                    $"WICHTIG: Auto-Update bis v{maxAuto}. Ab v{offer.MigrationNotice.ManualInstallVersion} manuell installieren.");
            return new[] { header, warning }.Concat(offer.Changelog).ToList();
        }

        private void ShowMigrationNotice(UpdateMigrationNotice.Info notice)
        {
            this.phase = UpdateFormPhase.MigrationNotice;
            this.HideErrorDetails();

            var maxAuto = notice.MaxAutoUpdateVersion;
            this.titleLabel.Text = string.IsNullOrWhiteSpace(maxAuto)
                ? LauncherLocalization.L(
                    $"Manual update required for v{notice.ManualInstallVersion}",
                    $"Manuelles Update fuer v{notice.ManualInstallVersion} erforderlich")
                : LauncherLocalization.L(
                    $"Install v{notice.ManualInstallVersion} manually",
                    $"v{notice.ManualInstallVersion} manuell installieren");

            this.subtitleLabel.Text = string.IsNullOrWhiteSpace(maxAuto)
                ? LauncherLocalization.L(
                    $"Your installed version will not auto-update to v{notice.ManualInstallVersion}.",
                    $"Die installierte Version wird nicht automatisch auf v{notice.ManualInstallVersion} aktualisiert.")
                : LauncherLocalization.L(
                    $"Auto-update works up to v{maxAuto}. Use GameHelperDownloader.exe or the ZIP for v{notice.ManualInstallVersion}+.",
                    $"Auto-Update bis v{maxAuto}. Fuer v{notice.ManualInstallVersion}+ GameHelperDownloader.exe oder ZIP verwenden.");

            this.statusLabel.Text = LauncherLocalization.L(notice.MessageEn, notice.MessageDe);

            this.progressBar.Visible = false;
            this.changelogTabs.Visible = false;

            this.primaryButton.Text = LauncherLocalization.L("Continue", "Weiter");
            this.secondaryButton.Text = LauncherLocalization.L("Open download page", "Download-Seite oeffnen");
            this.primaryButton.Enabled = true;
            this.secondaryButton.Enabled = true;
            this.proxyButton.Enabled = true;
        }

        private void ShowPrompt(UpdateOffer offer)

        {

            this.phase = UpdateFormPhase.Prompt;

            this.HideErrorDetails();

            this.titleLabel.Text = offer.MigrationNotice != null
                ? LauncherLocalization.L(
                    $"Update v{offer.RemoteVersion} (read notice)",
                    $"Update v{offer.RemoteVersion} (Hinweis lesen)")
                : LauncherLocalization.L(
                $"Update v{offer.RemoteVersion} available",
                $"Update v{offer.RemoteVersion} verfuegbar");

            this.subtitleLabel.Text = LauncherLocalization.L(

                offer.IsZipUpdate
                    ? $"Current: v{offer.CurrentVersion}  ->  New: v{offer.RemoteVersion} (full package)"
                    : $"Current: v{offer.CurrentVersion}  ->  New: v{offer.RemoteVersion} ({offer.FileCount} files)",

                offer.IsZipUpdate
                    ? $"Aktuell: v{offer.CurrentVersion}  ->  Neu: v{offer.RemoteVersion} (Vollstaendiges Paket)"
                    : $"Aktuell: v{offer.CurrentVersion}  ->  Neu: v{offer.RemoteVersion} ({offer.FileCount} Dateien)");

            this.statusLabel.Text = LauncherLocalization.L("Release notes:", "Release Notes:");

            this.progressBar.Visible = false;

            this.changelogTabs.Visible = true;

            this.changelogTabs.SelectedTab = this.tabCurrentRelease;

            this.FillChangelogList(this.BuildChangelogLines(offer));



            this.primaryButton.Text = LauncherLocalization.L(

                $"Update now (v{offer.RemoteVersion})",

                $"Jetzt aktualisieren (v{offer.RemoteVersion})");

            this.secondaryButton.Text = LauncherLocalization.L("Start without update", "Ohne Update starten");

            this.primaryButton.Enabled = true;

            this.secondaryButton.Enabled = true;

            this.proxyButton.Enabled = true;

        }



        private async void BeginDownload()

        {

            await this.ShowDownloadAsync();

        }



        private async Task ShowDownloadAsync()

        {

            if (this.currentOffer == null)

            {

                return;

            }



            this.phase = UpdateFormPhase.Downloading;

            this.HideErrorDetails();

            this.titleLabel.Text = LauncherLocalization.L("Downloading update", "Update wird heruntergeladen");

            this.subtitleLabel.Text = $"v{this.currentOffer.RemoteVersion}";

            this.statusLabel.Text = LauncherLocalization.L("Download in progress...", "Download laeuft...");

            this.progressBar.Visible = true;

            this.progressBar.Value = 0;

            this.changelogTabs.Visible = false;

            this.primaryButton.Enabled = false;

            this.secondaryButton.Enabled = false;

            this.proxyButton.Enabled = false;



            this.downloadCts = new CancellationTokenSource();

            var progress = new Progress<DownloadProgress>(p =>

            {

                this.progressBar.Value = Math.Clamp(p.Percent, 0, 100);

                this.statusLabel.Text = string.IsNullOrEmpty(p.CurrentFile)

                    ? LauncherLocalization.L("Download complete.", "Download abgeschlossen.")

                    : LauncherLocalization.L(

                        $"Downloading ({p.CompletedFiles + 1}/{p.TotalFiles}):{Environment.NewLine}{p.CurrentFile}",

                        $"Lade ({p.CompletedFiles + 1}/{p.TotalFiles}):{Environment.NewLine}{p.CurrentFile}");

            });



            try

            {

                await UpdateService.DownloadUpdateAsync(

                    this.currentOffer,

                    this.installDir,

                    progress,

                    this.downloadCts.Token);

                this.ShowReadyToRestart(this.currentOffer);

            }

            catch (OperationCanceledException)

            {

                this.ShowError(LauncherLocalization.L("Download cancelled.", "Download abgebrochen."));

            }

            catch (Exception ex)

            {

                LauncherLog.Write($"Download: {ex}");

                var detail = UpdateErrors.Format(ex);

                this.ShowError(LauncherLocalization.L(

                    $"Download failed:{Environment.NewLine}{detail}",

                    $"Download fehlgeschlagen:{Environment.NewLine}{detail}"));

            }

        }



        private void ShowReadyToRestart(UpdateOffer offer)

        {

            this.phase = UpdateFormPhase.ReadyToInstall;

            this.HideErrorDetails();

            this.titleLabel.Text = LauncherLocalization.L("Update ready", "Update bereit");

            this.subtitleLabel.Text = LauncherLocalization.L(

                $"Version {offer.RemoteVersion} has been downloaded.",

                $"Version {offer.RemoteVersion} wurde heruntergeladen.");

            this.statusLabel.Text = LauncherLocalization.L("Release notes:", "Release Notes:");

            this.progressBar.Visible = false;

            this.changelogTabs.Visible = true;

            this.changelogTabs.SelectedTab = this.tabCurrentRelease;

            this.FillChangelogList(offer.Changelog);



            this.primaryButton.Text = LauncherLocalization.L("Restart and install", "Neustarten und installieren");

            this.secondaryButton.Text = LauncherLocalization.L("Later", "Spaeter");

            this.primaryButton.Enabled = true;

            this.secondaryButton.Enabled = true;

            this.proxyButton.Enabled = true;

        }



        private static string NormalizeVersion(string version) =>

            version.Trim().TrimStart('v', 'V');



        private IReadOnlyList<string> GetDisplayChangelogRaw(IReadOnlyList<string> manifestLines)

        {

            var version = this.currentOffer?.RemoteVersion;

            if (string.IsNullOrEmpty(version))

            {

                return manifestLines;

            }

            var entry = this.releaseHistory.FirstOrDefault(r =>

                string.Equals(NormalizeVersion(r.Version), NormalizeVersion(version), StringComparison.OrdinalIgnoreCase));

            if (entry?.Changelog == null || entry.Changelog.Count == 0)

            {

                return manifestLines;

            }

            var manifestBilingual = manifestLines.Any(ChangelogLocalization.LooksBilingual);

            var historyBilingual = entry.Changelog.Any(ChangelogLocalization.LooksBilingual);

            return historyBilingual || !manifestBilingual ? entry.Changelog : manifestLines;

        }



        private void FillChangelogList(IReadOnlyList<string> rawLines)

        {

            this.currentChangelogRaw = this.GetDisplayChangelogRaw(rawLines);

            var lines = ChangelogLocalization.ResolveLines(this.currentChangelogRaw).ToList();

            if (lines.Count == 0)

            {

                lines.Add(LauncherLocalization.L(

                    "Improvements and bug fixes.",

                    "Verbesserungen und Fehlerbehebungen."));

            }

            this.currentReleaseBox.Text = string.Join(

                Environment.NewLine,

                lines.Select(line => $"• {line}"));

            this.currentReleaseBox.SelectionStart = 0;

            this.currentReleaseBox.ScrollToCaret();

        }



        private void FillHistoryBox()

        {

            this.historyBox.Text = ChangelogHistoryService.FormatForDisplay(this.releaseHistory);

            this.historyBox.SelectionStart = 0;

            this.historyBox.ScrollToCaret();

        }



        private void ShowError(string message)

        {

            this.phase = UpdateFormPhase.Error;

            this.titleLabel.Text = LauncherLocalization.L("Update unavailable", "Update nicht moeglich");

            this.subtitleLabel.Text = string.Empty;

            this.statusLabel.Text = LauncherLocalization.L("Details:", "Details:");

            this.errorBox.Visible = true;

            this.errorBox.Text = message;

            this.errorBox.SelectionStart = 0;

            this.errorBox.ScrollToCaret();

            this.progressBar.Visible = false;

            this.changelogTabs.Visible = false;

            this.primaryButton.Text = LauncherLocalization.L("Start without update", "Ohne Update starten");

            this.secondaryButton.Text = LauncherLocalization.L("Exit", "Beenden");

            this.primaryButton.Enabled = true;

            this.secondaryButton.Enabled = true;

            this.proxyButton.Enabled = true;

        }



        private void OnPrimaryClick(object? sender, EventArgs e)

        {

            switch (this.phase)

            {

                case UpdateFormPhase.MigrationNotice:

                    this.ShouldStartGame = true;

                    this.Close();

                    return;



                case UpdateFormPhase.Prompt:

                    this.primaryButton.Enabled = false;

                    this.secondaryButton.Enabled = false;

                    this.BeginDownload();

                    return;



                case UpdateFormPhase.ReadyToInstall:

                    this.InstallAndExit();

                    return;



                case UpdateFormPhase.Error:

                default:

                    this.ShouldStartGame = true;

                    this.Close();

                    return;

            }

        }



        private void InstallAndExit()

        {

            try

            {

                if (!UpdateService.HasStagedUpdate)

                {

                    throw new InvalidOperationException(

                        LauncherLocalization.L(

                            "Downloaded files are missing. Please run the update again.",

                            "Heruntergeladene Dateien fehlen. Bitte Update erneut ausfuehren."));

                }



                this.primaryButton.Enabled = false;

                this.secondaryButton.Enabled = false;

                this.statusLabel.Text = LauncherLocalization.L(

                    "Installing update and restarting...",

                    "Installiere Update und starte neu...");

                UpdateService.ApplyUpdateAndRestart();

                Environment.Exit(0);

            }

            catch (Exception ex)

            {

                LauncherLog.Write($"Install: {ex}");

                var message = ex.Message.Contains("Zugriff verweigert", StringComparison.OrdinalIgnoreCase) ||

                              ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)

                    ? LauncherLocalization.L(

                        "Could not start the update helper (access denied). Run GameHelper as administrator, or install to a folder such as Documents\\GameHelper.",

                        "Update-Helfer konnte nicht gestartet werden (Zugriff verweigert). GameHelper als Administrator starten oder in einen Ordner wie Dokumente\\GameHelper installieren.")

                    : ex.Message;

                MessageBox.Show(

                    message,

                    "GameHelper",

                    MessageBoxButtons.OK,

                    MessageBoxIcon.Error);

                this.primaryButton.Enabled = true;

                this.secondaryButton.Enabled = true;

            }

        }



        private void OnSecondaryClick(object? sender, EventArgs e)

        {

            switch (this.phase)

            {

                case UpdateFormPhase.MigrationNotice:

                    try

                    {

                        Process.Start(new ProcessStartInfo

                        {

                            FileName = $"{UpdateRepositoryConfig.GitHubHost}/{UpdateRepositoryConfig.Repository}/releases/latest",

                            UseShellExecute = true,

                        });

                    }

                    catch (Exception ex)

                    {

                        LauncherLog.Write($"Open releases: {ex}");

                    }

                    return;



                case UpdateFormPhase.Prompt:

                case UpdateFormPhase.ReadyToInstall:

                    this.ShouldStartGame = true;

                    this.Close();

                    return;



                case UpdateFormPhase.Error:

                    Environment.Exit(0);

                    return;



                default:

                    this.ShouldStartGame = true;

                    this.Close();

                    return;

            }

        }

    }

}


