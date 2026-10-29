using Android.App;
using Android.Content;
using Android.Media.Projection;
using Android.OS;
using Android.Text.Method;
using Android.Widget;

namespace ConsentCapture.Android;

[Activity(Label = "Consent Capture", MainLauncher = true, Exported = true)]
public class MainActivity : Activity
{
    private const int CaptureRequest = 2201;
    private const int NotificationRequest = 2202;
    private const string PreferencesName = "ConsentCapture";
    private EditText portalUrl = null!;
    private EditText accessToken = null!;
    private CheckBox consent = null!;
    private Button startButton = null!;
    private Button stopButton = null!;
    private TextView status = null!;
    private ISharedPreferences preferences = null!;
    private bool waitingForNotificationPermission;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        preferences = GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;
        BuildUi();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (status is null) return;
        var active = preferences.GetBoolean("captureActive", false);
        var savedStatus = preferences.GetString("captureStatus", "");
        if (!string.IsNullOrWhiteSpace(savedStatus)) status.Text = savedStatus;
        UpdateButtons(active);
    }

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(28, 32, 28, 24);
        root.SetBackgroundColor(global::Android.Graphics.Color.Rgb(245, 248, 252));
        var scroll = new ScrollView(this); scroll.AddView(root); SetContentView(scroll);

        var title = new TextView(this) { Text = "Consent Capture", TextSize = 24, Typeface = global::Android.Graphics.Typeface.DefaultBold };
        root.AddView(title);
        var desc = new TextView(this)
        {
            Text = "When you tap Start and approve Android's screen-sharing prompt, this app uploads a screenshot every 10 seconds. Android shows an ongoing sharing notification with a Stop action. Stop the session at any time from the app or notification.",
            TextSize = 15
        };
        desc.SetTextColor(global::Android.Graphics.Color.Rgb(79, 94, 113)); desc.SetPadding(0, 12, 0, 18); root.AddView(desc);

        portalUrl = new EditText(this)
        {
            Hint = "Portal URL (http://... or https://...)",
            Text = preferences.GetString("portalUrl", "http://192.168.0.155:5071"),
            InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationUri
        };
        portalUrl.SetSingleLine(true);
        accessToken = new EditText(this)
        {
            Hint = "Portal upload token",
            Text = preferences.GetString("accessToken", ""),
            InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword
        };
        accessToken.SetSingleLine(true);
        accessToken.TransformationMethod = PasswordTransformationMethod.Instance;
        root.AddView(portalUrl); root.AddView(accessToken);

        consent = new CheckBox(this)
        {
            Text = "I consent to share this device's screen with the portal during an active session.",
            TextSize = 14,
            Checked = preferences.GetBoolean("consent", false)
        };
        consent.SetPadding(0, 12, 0, 12); root.AddView(consent);
        startButton = new Button(this) { Text = "Start screen sharing" }; root.AddView(startButton);
        stopButton = new Button(this) { Text = "Stop screen sharing" }; root.AddView(stopButton);
        status = new TextView(this) { Text = "Screen capture starts only after you tap Start and approve Android's prompt.", TextSize = 14 };
        status.SetPadding(0, 12, 0, 0); root.AddView(status);

        consent.CheckedChange += (_, e) =>
        {
            preferences.Edit()!.PutBoolean("consent", e.IsChecked)!.Apply();
            status.Text = e.IsChecked ? "Consent saved on this device. Tap Start to begin a session." : "Consent revoked.";
        };
        startButton.Click += (_, _) => BeginStartFlow();
        stopButton.Click += (_, _) => StopSharing();
        UpdateButtons(false);
    }

    private void UpdateButtons(bool sharing)
    {
        startButton.Enabled = consent?.Checked == true && !sharing;
        stopButton.Enabled = sharing;
    }

    private void BeginStartFlow()
    {
        if (!consent.Checked) { status.Text = "Check the consent box before starting."; return; }
        var url = portalUrl.Text?.Trim() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != "https" && parsed.Scheme != "http"))
        { status.Text = "Enter a valid portal URL (http or https)."; return; }
        var token = accessToken.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(token)) { status.Text = "Enter the portal upload token."; return; }
        preferences.Edit()!.PutString("portalUrl", url)!.PutString("accessToken", token)!.Apply();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
        {
            waitingForNotificationPermission = true;
            RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, NotificationRequest);
            return;
        }
        RequestScreenCaptureConsent();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, global::Android.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != NotificationRequest || !waitingForNotificationPermission) return;
        waitingForNotificationPermission = false;
        if (grantResults.Length > 0 && grantResults[0] == global::Android.Content.PM.Permission.Granted)
            RequestScreenCaptureConsent();
        else
            status.Text = "Allow notifications so Android can show the required active screen-sharing notice.";
    }

    private void RequestScreenCaptureConsent()
    {
        var manager = (MediaProjectionManager?)GetSystemService(MediaProjectionService);
        if (manager is null) { status.Text = "Android screen capture is unavailable on this device."; return; }
        status.Text = "Approve Android's screen-sharing prompt to start.";
        StartActivityForResult(manager.CreateScreenCaptureIntent(), CaptureRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != CaptureRequest) return;
        if (resultCode != Result.Ok || data is null)
        {
            status.Text = "Screen sharing was not started. Android permission is required for each session.";
            UpdateButtons(false);
            return;
        }

        var start = new Intent(this, typeof(ScreenCaptureService));
        start.SetAction(ScreenCaptureService.ActionStart);
        start.PutExtra(ScreenCaptureService.ExtraResultCode, (int)resultCode);
        start.PutExtra(ScreenCaptureService.ExtraProjectionData, data);
        start.PutExtra(ScreenCaptureService.ExtraPortalUrl, portalUrl.Text?.Trim());
        start.PutExtra(ScreenCaptureService.ExtraAccessToken, accessToken.Text?.Trim());
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(start); else StartService(start);
        status.Text = "Starting screen sharing…";
        UpdateButtons(true);
        new Handler(Looper.MainLooper!).PostDelayed(() =>
        {
            if (status is null) return;
            var active = preferences.GetBoolean("captureActive", false);
            var savedStatus = preferences.GetString("captureStatus", "");
            status.Text = string.IsNullOrWhiteSpace(savedStatus)
                ? (active ? "Sharing is active. Screenshots upload every 10 seconds." : "Sharing did not start. Tap Start and approve Android's prompt.")
                : savedStatus;
            UpdateButtons(active);
        }, 2000);
    }

    private void StopSharing()
    {
        var stop = new Intent(this, typeof(ScreenCaptureService));
        stop.SetAction(ScreenCaptureService.ActionStop);
        StartService(stop);
        preferences.Edit()!.PutBoolean("captureActive", false)!.PutString("captureStatus", "Screen sharing stopped.")!.Apply();
        status.Text = "Screen sharing stopped.";
        UpdateButtons(false);
    }
}
