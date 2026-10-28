using System.Net.Http.Headers;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;

namespace ConsentCapture.Android;

[Activity(Label = "Consent Capture", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    private EditText portalUrl = null!;
    private EditText accessToken = null!;
    private CheckBox consent = null!;
    private Button chooseButton = null!;
    private Button uploadButton = null!;
    private TextView selectedLabel = null!;
    private TextView status = null!;
    private global::Android.Net.Uri? selectedUri;
    private string selectedMime = "image/jpeg";
    private string selectedName = "selected-image.jpg";
    private ISharedPreferences preferences = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        preferences = GetSharedPreferences("ConsentCapture", FileCreationMode.Private)!;
        BuildUi();
    }

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(28, 32, 28, 20);
        root.SetBackgroundColor(global::Android.Graphics.Color.Rgb(245, 248, 252));
        var scroll = new ScrollView(this); scroll.AddView(root);
        SetContentView(scroll);

        var title = new TextView(this) { Text = "Consent Capture", TextSize = 24, Typeface = global::Android.Graphics.Typeface.DefaultBold };
        title.SetTextColor(global::Android.Graphics.Color.Rgb(24, 34, 48)); root.AddView(title);
        var desc = new TextView(this) { Text = "Choose an image or screenshot using Android's system picker. The app uploads only the image you select after you tap Upload. It does not capture the screen or upload in the background.", TextSize = 15 };
        desc.SetTextColor(global::Android.Graphics.Color.Rgb(79, 94, 113)); desc.SetPadding(0, 12, 0, 18); root.AddView(desc);

        portalUrl = new EditText(this) { Hint = "Portal URL (https://...)", InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationUri };
        portalUrl.SetSingleLine(true);
        accessToken = new EditText(this) { Hint = "Portal upload token", InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword };
        accessToken.SetSingleLine(true);
        root.AddView(portalUrl); root.AddView(accessToken);

        consent = new CheckBox(this) { Text = "I consent to upload images I select to this portal.", TextSize = 14, Checked = preferences.GetBoolean("consent", false) };
        consent.SetPadding(0, 12, 0, 12); root.AddView(consent);
        chooseButton = new Button(this) { Text = "Choose image or screenshot" }; root.AddView(chooseButton);
        selectedLabel = new TextView(this) { Text = "No image selected", TextSize = 14 }; selectedLabel.SetPadding(0, 8, 0, 4); root.AddView(selectedLabel);
        uploadButton = new Button(this) { Text = "Upload selected image", Enabled = false }; root.AddView(uploadButton);
        status = new TextView(this) { Text = "Consent is stored on this device and can be revoked here.", TextSize = 14 }; status.SetPadding(0, 10, 0, 0); root.AddView(status);

        consent.CheckedChange += (_, e) => { preferences.Edit()!.PutBoolean("consent", e.IsChecked)!.Apply(); UpdateButtons(); status.Text = e.IsChecked ? "Consent saved on this device." : "Consent revoked."; };
        chooseButton.Click += (_, _) => OpenPicker();
        uploadButton.Click += async (_, _) => await UploadAsync();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var allowed = consent?.Checked == true;
        chooseButton.Enabled = allowed;
        uploadButton.Enabled = allowed && selectedUri is not null;
    }

    private void OpenPicker()
    {
        if (!consent.Checked) return;
        var intent = new Intent(Intent.ActionGetContent);
        intent.SetType("image/*");
        intent.AddCategory(Intent.CategoryOpenable);
        StartActivityForResult(Intent.CreateChooser(intent, "Select an image to upload"), 1201);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != 1201 || resultCode != Result.Ok || data?.Data is null) return;
        selectedUri = data.Data;
        selectedMime = ContentResolver?.GetType(selectedUri) ?? "image/jpeg";
        selectedName = "selected-image" + (selectedMime == "image/png" ? ".png" : ".jpg");
        selectedLabel.Text = "Selected: " + selectedName;
        status.Text = "Image selected. Tap Upload to send it.";
        UpdateButtons();
    }

    private async Task UploadAsync()
    {
        if (!consent.Checked || selectedUri is null) return;
        global::Android.Net.Uri? parsed;
        try { parsed = global::Android.Net.Uri.Parse(portalUrl.Text?.Trim()); }
        catch { parsed = null; }
        if (parsed?.Scheme != "https" || string.IsNullOrWhiteSpace(parsed.Host))
        { status.Text = "Enter the portal's HTTPS URL."; return; }
        var bearer = accessToken.Text?.Trim();
        if (string.IsNullOrWhiteSpace(bearer)) { status.Text = "Enter the portal upload token."; return; }
        if (selectedMime is not ("image/jpeg" or "image/png")) { status.Text = "The portal accepts JPEG or PNG. Choose a screenshot or image in one of those formats."; return; }
        uploadButton.Enabled = false; chooseButton.Enabled = false; status.Text = "Uploading selected image…";
        try
        {
            await using var source = ContentResolver!.OpenInputStream(selectedUri);
            if (source is null) throw new InvalidOperationException("Android could not open the selected image.");
            await using var buffer = new MemoryStream(); await source.CopyToAsync(buffer);
            if (buffer.Length == 0 || buffer.Length > 10 * 1024 * 1024) throw new InvalidOperationException("Choose a nonempty image up to 10 MB.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var form = new MultipartFormDataContent();
            using var image = new ByteArrayContent(buffer.ToArray());
            image.Headers.ContentType = MediaTypeHeaderValue.Parse(selectedMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? selectedMime : "image/jpeg");
            form.Add(image, "image", selectedName);
            var endpoint = new System.Uri((parsed!.ToString() ?? string.Empty).TrimEnd('/') + "/api/images");
            using var response = await client.PostAsync(endpoint, form);
            var responseContent = response.Content;
            var errorBody = responseContent is null ? string.Empty : await responseContent.ReadAsStringAsync();
            status.Text = response.IsSuccessStatusCode ? "Upload complete. Open the portal and load images to view it." : $"Portal returned {(int)response.StatusCode}: {errorBody}";
        }
        catch (Exception ex) { status.Text = "Upload failed: " + ex.Message; }
        finally { UpdateButtons(); }
    }
}
