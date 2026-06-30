namespace Launcher
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    internal sealed class ProxySettingsForm : Form
    {
        private readonly RadioButton githubProxyRadio = new();
        private readonly RadioButton networkProxyRadio = new();
        private readonly RadioButton directRadio = new();
        private readonly TextBox githubProxyTextBox = new();
        private readonly TextBox networkProxyTextBox = new();

        internal ProxySettingsForm(UpdateProxyOptions options)
        {
            this.Text = LauncherLocalization.L("Update proxy", "Update-Proxy");
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(520, 214);
            this.Font = new Font("Segoe UI", 9F);

            this.BuildLayout();
            this.LoadOptions(options);
            this.UpdateInputState();
        }

        internal UpdateProxyOptions Options { get; private set; } = new();

        private void BuildLayout()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 5,
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            this.Controls.Add(panel);

            this.githubProxyRadio.AutoSize = true;
            this.githubProxyRadio.Text = LauncherLocalization.L("GitHub URL proxy", "GitHub-URL-Proxy");
            this.githubProxyRadio.CheckedChanged += (_, _) => this.UpdateInputState();
            panel.Controls.Add(this.githubProxyRadio, 0, 0);

            this.githubProxyTextBox.Dock = DockStyle.Fill;
            this.githubProxyTextBox.PlaceholderText = "https://gh-proxy.com/;https://your-proxy.example/";
            panel.Controls.Add(this.githubProxyTextBox, 1, 0);

            this.networkProxyRadio.AutoSize = true;
            this.networkProxyRadio.Text = LauncherLocalization.L("HTTP / SOCKS proxy", "HTTP- / SOCKS-Proxy");
            this.networkProxyRadio.CheckedChanged += (_, _) => this.UpdateInputState();
            panel.Controls.Add(this.networkProxyRadio, 0, 1);

            this.networkProxyTextBox.Dock = DockStyle.Fill;
            this.networkProxyTextBox.PlaceholderText = "http://127.0.0.1:7890 or socks5://127.0.0.1:1080";
            panel.Controls.Add(this.networkProxyTextBox, 1, 1);

            this.directRadio.AutoSize = true;
            this.directRadio.Text = LauncherLocalization.L("Direct GitHub", "GitHub direkt");
            this.directRadio.CheckedChanged += (_, _) => this.UpdateInputState();
            panel.Controls.Add(this.directRadio, 0, 2);

            var hint = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                Text = LauncherLocalization.L(
                    "The launcher still verifies manifest signatures and ZIP hashes after download.",
                    "Der Launcher prueft weiterhin Manifest-Signaturen und ZIP-Hashes nach dem Download."),
            };
            panel.SetColumnSpan(hint, 2);
            panel.Controls.Add(hint, 0, 3);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
            };
            panel.SetColumnSpan(buttons, 2);
            panel.Controls.Add(buttons, 0, 4);

            var okButton = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.OK,
                Text = "OK",
            };
            okButton.Click += this.OkButton_Click;
            buttons.Controls.Add(okButton);

            var cancelButton = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.Cancel,
                Text = LauncherLocalization.L("Cancel", "Abbrechen"),
            };
            buttons.Controls.Add(cancelButton);

            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }

        private void LoadOptions(UpdateProxyOptions options)
        {
            this.githubProxyTextBox.Text = options.GitHubProxyBaseUrls;
            this.networkProxyTextBox.Text = options.NetworkProxyUrl;

            switch (options.Mode)
            {
                case UpdateProxyMode.NetworkProxy:
                    this.networkProxyRadio.Checked = true;
                    break;
                case UpdateProxyMode.Direct:
                    this.directRadio.Checked = true;
                    break;
                default:
                    this.githubProxyRadio.Checked = true;
                    break;
            }
        }

        private void UpdateInputState()
        {
            this.githubProxyTextBox.Enabled = this.githubProxyRadio.Checked;
            this.networkProxyTextBox.Enabled = this.networkProxyRadio.Checked;
        }

        private void OkButton_Click(object? sender, EventArgs e)
        {
            if (this.networkProxyRadio.Checked && string.IsNullOrWhiteSpace(this.networkProxyTextBox.Text))
            {
                MessageBox.Show(
                    this,
                    LauncherLocalization.L("Enter an HTTP or SOCKS proxy URL.", "HTTP- oder SOCKS-Proxy-URL eingeben."),
                    this.Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }

            this.Options = new UpdateProxyOptions
            {
                Mode = this.networkProxyRadio.Checked
                    ? UpdateProxyMode.NetworkProxy
                    : this.directRadio.Checked
                        ? UpdateProxyMode.Direct
                        : UpdateProxyMode.GitHubProxy,
                GitHubProxyBaseUrls = this.githubProxyTextBox.Text.Trim(),
                NetworkProxyUrl = this.networkProxyTextBox.Text.Trim(),
            };

            try
            {
                this.Options.GetNetworkProxyUri();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
            }
        }
    }
}
