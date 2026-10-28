# Consent Capture demo

This package contains a visible Windows desktop app and an authenticated ASP.NET Core image portal. The ZIP reference was used for the general .NET agent/backend structure; this implementation does not install a background service, run at startup, hide its window, or capture on a timer.

There is also an Android APK source project. The Android app asks once for app-level consent, then lets the user choose a JPEG or PNG from Android's system picker and upload it with a button. It does not silently capture the display. Android's own screen-capture API requires a fresh system consent prompt for each capture session.

## Desktop app

Run `publish/CaptureApp/ConsentCapture.exe` on Windows 10/11. On first launch, the user sees a consent notice. The choice is remembered for that Windows account. The main window stays visible, and each image requires clicking **Capture and upload** and confirming the capture. **Revoke consent** disables capture and removes the saved consent choice.

Enter the portal base URL and the upload token in the app. Use an HTTPS URL for a remote portal. HTTP is accepted only for localhost development.

## Portal on the same PC

Open PowerShell in `publish/Portal` and run:

```powershell
$env:PORTAL_UPLOAD_TOKEN = "choose-a-long-random-secret"
./Portal.exe
```

Open `http://localhost:5071` in a browser. Enter the same token and choose **Load images**. In the desktop app, use `http://localhost:5071` as the portal URL and enter the same token. Uploaded images are stored under `publish/Portal/App_Data/images`.

## Portal on another device

Host the portal behind HTTPS on a trusted server, set `PORTAL_UPLOAD_TOKEN` in the server environment, and set `PORTAL_URL` to the HTTPS listener URL. Configure the reverse proxy/firewall and persistent storage for that host. Enter the public HTTPS base URL and the token into the desktop app. Do not expose the demo's token directly to untrusted users; for a production deployment, use per-device credentials and a proper user sign-in system.

## Contents

- `publish/CaptureApp/ConsentCapture.exe` — self-contained Windows x64 desktop app.
- `publish/Portal/Portal.exe` and `publish/Portal/wwwroot/` — self-contained portal server and web page.
- `AndroidApp/` — source for the Android client APK.
- `CaptureApp/` and `Portal/` — source projects.

The portal accepts JPEG or PNG uploads up to 10 MB. The shared token protects image upload, listing, and viewing. HTTPS protects remote traffic in transit; images are stored as files on the portal host, so production deployments should use encrypted storage, backups, access controls, retention rules, and per-user authentication.

## Current Render setup

The portal gallery is deployed alongside the existing Phone Share app at [phone-share.onrender.com](https://phone-share.onrender.com). The deployed source is in [yadveshyadav/Phone-share-apk](https://github.com/yadveshyadav/Phone-share-apk), and its existing free Render service keeps recent images in memory only. It holds at most 20 images or 48 MB; images may disappear when the free service sleeps, restarts, or redeploys.

Use `https://phone-share.onrender.com` as the portal URL in the Android app. The upload token is stored locally in `../PhoneShare-Portal-Access.txt` and configured in the Render service environment. Do not commit or post that token. The separate `render.yaml` in this source repo describes an optional paid standalone portal and is not used by the current deployment.
