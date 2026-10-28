using System.Drawing.Imaging;
using System.Net.Http.Headers;

namespace ConsentCapture;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly string consentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConsentCapture", "consent.txt");
    private readonly TextBox server = new() { PlaceholderText = "https://your-portal.example", Width = 360 };
    private readonly TextBox token = new() { PlaceholderText = "Portal access token", Width = 360, UseSystemPasswordChar = true };
    private readonly Button capture = new() { Text = "Capture and upload", AutoSize = true, Enabled = false };
    private readonly Button revoke = new() { Text = "Revoke consent", AutoSize = true, Enabled = false };
    private readonly Label status = new() { AutoSize = true, Text = "Not authorized. Review and accept the consent notice to enable capture." };
    private bool authorized;

    public MainForm()
    {
        Text = "Consent Capture";
        Width = 590; Height = 390; StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(590, 390);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(22), AutoScroll = true };
        Controls.Add(panel);
        panel.Controls.Add(new Label { Text = "Visible, user-controlled screen sharing", Font = new Font("Segoe UI", 16, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 4, 3, 12) });
        panel.Controls.Add(new Label { Text = "This app captures your entire screen only when you click Capture and upload. The image is sent to the portal you enter below. There is no background or scheduled capture. Consent is stored on this Windows account and can be revoked here.", AutoSize = true, MaximumSize = new Size(510, 0), Margin = new Padding(3, 3, 3, 14) });
        panel.Controls.Add(new Label { Text = "Portal URL", AutoSize = true }); panel.Controls.Add(server);
        panel.Controls.Add(new Label { Text = "Upload token (ask your portal administrator)", AutoSize = true }); panel.Controls.Add(token);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 12, 0, 8) };
        actions.Controls.Add(capture); actions.Controls.Add(revoke); panel.Controls.Add(actions);
        status.MaximumSize = new Size(510, 0); panel.Controls.Add(status);
        capture.Click += async (_, _) => await CaptureAndUploadAsync();
        revoke.Click += (_, _) => RevokeConsent();
        Shown += (_, _) => CheckConsent();
    }

    private void CheckConsent()
    {
        if (File.Exists(consentPath) && File.ReadAllText(consentPath).Trim() == "accepted-v1")
        {
            authorized = true; capture.Enabled = true; revoke.Enabled = true;
            status.Text = "Consent is active. Capture happens only when you click the button.";
            return;
        }
        using var dialog = new ConsentDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(consentPath)!);
            File.WriteAllText(consentPath, "accepted-v1");
            authorized = true; capture.Enabled = true; revoke.Enabled = true;
            status.Text = "Consent saved for this Windows account. Capture is user-initiated.";
        }
        else status.Text = "Consent not granted. No screen data will be captured or sent.";
    }

    private void RevokeConsent()
    {
        authorized = false; capture.Enabled = false; revoke.Enabled = false;
        if (File.Exists(consentPath)) File.Delete(consentPath);
        status.Text = "Consent revoked. Close and reopen the app to review the consent notice again.";
    }

    private async Task CaptureAndUploadAsync()
    {
        if (!authorized) return;
        if (!Uri.TryCreate(server.Text.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.IsLoopback && baseUri.Scheme == Uri.UriSchemeHttp)))
        { status.Text = "Use an HTTPS portal URL (HTTP is allowed only for localhost testing)."; return; }
        if (string.IsNullOrWhiteSpace(token.Text)) { status.Text = "Enter the portal upload token."; return; }
        if (MessageBox.Show(this, "Capture the screen now and upload this image to the portal?", "Confirm screen capture", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        capture.Enabled = false; status.Text = "Capturing and uploading…";
        try
        {
            var bounds = SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            await using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Jpeg);
            stream.Position = 0;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Text.Trim());
            using var content = new MultipartFormDataContent();
            using var image = new StreamContent(stream); image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(image, "image", "capture.jpg");
            var endpoint = new Uri(baseUri, "/api/images");
            using var response = await client.PostAsync(endpoint, content);
            status.Text = response.IsSuccessStatusCode ? "Upload complete. The image is available in the portal." : $"Portal returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
        }
        catch (Exception ex) { status.Text = "Upload failed: " + ex.Message; }
        finally { capture.Enabled = authorized; }
    }
}

internal sealed class ConsentDialog : Form
{
    private readonly CheckBox agree = new() { Text = "I understand and consent to this app capturing and sending my screen only when I click Capture and upload.", AutoSize = true };
    private readonly Button accept = new() { Text = "Accept and continue", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
    public ConsentDialog()
    {
        Text = "One-time consent"; Width = 520; Height = 250; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20) }; Controls.Add(panel);
        panel.Controls.Add(new Label { Text = "Consent required", Font = new Font("Segoe UI", 14, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 3, 3, 12) });
        panel.Controls.Add(new Label { Text = "Screen images are uploaded only after you confirm each capture. The app remains visible. You can revoke saved consent at any time in the main window.", AutoSize = true, MaximumSize = new Size(455, 0), Margin = new Padding(3, 3, 3, 12) });
        panel.Controls.Add(agree); panel.Controls.Add(accept); agree.CheckedChanged += (_, _) => accept.Enabled = agree.Checked;
        AcceptButton = accept; CancelButton = new Button { DialogResult = DialogResult.Cancel };
    }
}
