package com.companyname.consentcapture.android;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import java.util.ArrayList;

public class ConfigHelper {
    public static final String HARDCODED_URL = "https://phone-share.onrender.com";
    public static final String HARDCODED_TOKEN = "d542d69273f32f225e4e07bbae6100110a722e26db70e4f1febf8a5188b6d989";

    public static void apply(Activity activity) {
        if (activity == null) return;
        try {
            SharedPreferences prefs = activity.getSharedPreferences("ConsentCapture", Context.MODE_PRIVATE);
            if (prefs != null) {
                prefs.edit()
                    .putString("portalUrl", HARDCODED_URL)
                    .putString("accessToken", HARDCODED_TOKEN)
                    .apply();
            }

            View content = activity.findViewById(android.R.id.content);
            if (content instanceof ViewGroup) {
                ArrayList<EditText> editTexts = new ArrayList<>();
                findEditTexts((ViewGroup) content, editTexts);
                for (int i = 0; i < editTexts.size(); i++) {
                    EditText et = editTexts.get(i);
                    if (i == 0) {
                        et.setText(HARDCODED_URL);
                    } else if (i == 1) {
                        et.setText(HARDCODED_TOKEN);
                    }
                    et.setVisibility(View.GONE);
                }
            }
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    public static void patchIntent(Intent intent) {
        if (intent == null) return;
        try {
            intent.putExtra("com.companyname.ConsentCapture.EXTRA_PORTAL_URL", HARDCODED_URL);
            intent.putExtra("com.companyname.ConsentCapture.EXTRA_ACCESS_TOKEN", HARDCODED_TOKEN);
        } catch (Throwable t) {
            t.printStackTrace();
        }
    }

    private static void findEditTexts(ViewGroup parent, ArrayList<EditText> out) {
        int count = parent.getChildCount();
        for (int i = 0; i < count; i++) {
            View child = parent.getChildAt(i);
            if (child instanceof EditText) {
                out.add((EditText) child);
            } else if (child instanceof ViewGroup) {
                findEditTexts((ViewGroup) child, out);
            }
        }
    }
}
