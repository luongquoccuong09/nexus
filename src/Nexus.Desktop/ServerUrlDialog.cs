namespace Nexus.Desktop;

public sealed class ServerUrlDialog : Form
{
    private readonly TextBox _urlBox;
    private readonly Label _errorLabel;

    public string SelectedServerUrl { get; private set; } = string.Empty;

    public ServerUrlDialog(string currentUrl, Icon? appIcon)
    {
        Text = "Connect to Nexus";
        Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(470, 200);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label
        {
            Text = "Nexus Server Address",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(24, 34, 56),
            Location = new Point(20, 18),
            AutoSize = true
        };

        var subtitle = new Label
        {
            Text = "Enter your organization's HTTPS server URL (or http://127.0.0.1:5080 for local dev).",
            ForeColor = Color.FromArgb(90, 102, 125),
            Location = new Point(20, 48),
            Size = new Size(430, 34)
        };

        _urlBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(currentUrl) ? "https://" : currentUrl,
            Location = new Point(20, 88),
            Size = new Size(430, 28)
        };

        _errorLabel = new Label
        {
            ForeColor = Color.FromArgb(196, 43, 28),
            Location = new Point(20, 120),
            Size = new Size(430, 32)
        };

        var connectBtn = new Button
        {
            Text = "Connect",
            Location = new Point(276, 156),
            Size = new Size(84, 30),
            BackColor = Color.FromArgb(55, 67, 170),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        connectBtn.FlatAppearance.BorderSize = 0;
        connectBtn.Click += (_, _) => Confirm();

        var cancelBtn = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(366, 156),
            Size = new Size(84, 30)
        };

        AcceptButton = connectBtn;
        CancelButton = cancelBtn;

        Controls.AddRange([title, subtitle, _urlBox, _errorLabel, connectBtn, cancelBtn]);
    }

    private void Confirm()
    {
        if (!DesktopConfigStore.TryNormalizeServerUrl(_urlBox.Text, out var normalized, out var error))
        {
            _errorLabel.Text = error;
            return;
        }

        SelectedServerUrl = normalized;
        DialogResult = DialogResult.OK;
        Close();
    }
}
