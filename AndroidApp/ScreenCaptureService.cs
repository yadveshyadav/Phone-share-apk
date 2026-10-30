using System.Net.Http.Headers;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Views;

namespace ConsentCapture.Android;

[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaProjection)]
public sealed class ScreenCaptureService : Service
{
    public const string ActionStart = "com.inf.calculators.START";
    public const string ActionStop = "com.inf.calculators.STOP";
    public const string ExtraResultCode = "resultCode";
    public const string ExtraProjectionData = "projectionData";
    public const string ExtraPortalUrl = "portalUrl";
    public const string ExtraAccessToken = "accessToken";
    public static bool IsRunning { get; private set; }
    private const string ChannelId = "screen_sharing";
    private const string PreferencesName = "ConsentCapture";
    private const int NotificationId = 3141;
    private readonly object frameLock = new();
    private PowerManager.WakeLock? wakeLock;
    private MediaProjection? projection;
    private ProjectionCallback? projectionCallback;
    private VirtualDisplay? virtualDisplay;
    private ImageReader? imageReader;
    private HandlerThread? imageThread;
    private CancellationTokenSource? cancellation;
    private string portalUrl = "";
    private string accessToken = "";
    private DateTimeOffset lastUploadTime = DateTimeOffset.MinValue;
    private byte[]? latestJpegBytes;
    private bool isUploading;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopCapture();
            SetServiceState(false, "Screen sharing stopped.");
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (intent?.Action != ActionStart && intent?.Action != "com.companyname.ConsentCapture.START" && intent?.Action != "com.inf.calculators.START")
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        portalUrl = intent.GetStringExtra(ExtraPortalUrl) ?? "";
        accessToken = intent.GetStringExtra(ExtraAccessToken) ?? "";
        var resultCode = intent.GetIntExtra(ExtraResultCode, (int)Result.Canceled);
        var projectionData = global::Android.OS.Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            ? intent.GetParcelableExtra(ExtraProjectionData, Java.Lang.Class.FromType(typeof(Intent))) as Intent
            : intent.GetParcelableExtra(ExtraProjectionData) as Intent;
        if (resultCode != (int)Result.Ok || projectionData is null || string.IsNullOrWhiteSpace(accessToken))
        {
            SetServiceState(false, "Screen sharing was not authorized or portal token is missing.");
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        try
        {
            var notification = BuildNotification("Screen sharing active. Streaming updates & on-demand snapshots.");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, notification, global::Android.Content.PM.ForegroundService.TypeMediaProjection);
            else
                StartForeground(NotificationId, notification);

            var manager = (MediaProjectionManager?)GetSystemService(MediaProjectionService);
            projection = manager?.GetMediaProjection(resultCode, projectionData);
            if (projection is null) throw new InvalidOperationException("Android did not provide a screen capture session.");
            projectionCallback = new ProjectionCallback(this);
            projection.RegisterCallback(projectionCallback, new Handler(Looper.MainLooper!));
            StartVirtualDisplay();

            var pm = (PowerManager?)GetSystemService(PowerService);
            if (wakeLock is null && pm is not null)
            {
                wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "ConsentCapture::ScreenCaptureWakeLock");
                wakeLock.SetReferenceCounted(false);
                wakeLock.Acquire();
            }

            cancellation = new CancellationTokenSource();
            SetServiceState(true, "Sharing is active. Waiting for the first frame.");

            // Start background loop for auto-capture & portal snapshot click polling
            var token = cancellation.Token;
            _ = Task.Run(() => PollingAndAutoCaptureLoopAsync(token), token);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ConsentCapture", "Screen sharing service failed to start: " + ex);
            SetServiceState(false, "Could not start screen sharing: " + ex.Message);
            UpdateNotification("Could not start sharing: " + ex.Message);
            StopCapture();
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    private void StartVirtualDisplay()
    {
        var metrics = Resources?.DisplayMetrics ?? throw new InvalidOperationException("Screen size is unavailable.");
        var width = metrics.WidthPixels;
        var height = metrics.HeightPixels;
        if ((width & 1) != 0) width--;
        if ((height & 1) != 0) height--;
        var density = (int)metrics.DensityDpi;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Screen dimensions are invalid.");

        imageThread = new HandlerThread("ConsentCaptureFrames");
        imageThread.Start();
        var handler = new Handler(imageThread.Looper!);
        imageReader = ImageReader.NewInstance(width, height, (ImageFormatType)(int)Format.Rgba8888, 4);
        imageReader.SetOnImageAvailableListener(new FrameListener(this), handler);
        virtualDisplay = projection!.CreateVirtualDisplay(
            "Calculator", width, height, density,
            (DisplayFlags)(int)VirtualDisplayFlags.AutoMirror, imageReader.Surface, null, handler);
        if (virtualDisplay is null) throw new InvalidOperationException("Android could not create the screen capture display.");
    }

    private void OnFrameAvailable(ImageReader reader)
    {
        Image? image = null;
        try
        {
            image = reader.AcquireLatestImage();
            if (image is null) return;

            var plane = image.GetPlanes()?[0];
            if (plane is null) return;

            var width = image.Width;
            var height = image.Height;
            var pixelStride = plane.PixelStride;
            var rowStride = plane.RowStride;
            var buffer = plane.Buffer;
            if (buffer is null) return;

            Bitmap? finalBitmap = null;
            try
            {
                if (rowStride == width * pixelStride)
                {
                    finalBitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
                    finalBitmap.CopyPixelsFromBuffer(buffer);
                }
                else
                {
                    var paddedWidth = rowStride / pixelStride;
                    using var paddedBitmap = Bitmap.CreateBitmap(paddedWidth, height, Bitmap.Config.Argb8888!);
                    paddedBitmap.CopyPixelsFromBuffer(buffer);
                    finalBitmap = Bitmap.CreateBitmap(paddedBitmap, 0, 0, width, height);
                }

                using var output = new MemoryStream();
                if (finalBitmap.Compress(Bitmap.CompressFormat.Jpeg!, 82, output))
                {
                    var frame = output.ToArray();
                    bool shouldUploadNow = false;
                    lock (frameLock)
                    {
                        latestJpegBytes = frame;
                        var now = DateTimeOffset.UtcNow;
                        // Upload immediately on first frame, or if at least 15 seconds elapsed since last upload
                        if (lastUploadTime == DateTimeOffset.MinValue || (now - lastUploadTime).TotalSeconds >= 15)
                        {
                            shouldUploadNow = true;
                            lastUploadTime = now;
                        }
                    }

                    if (shouldUploadNow)
                    {
                        var token = cancellation?.Token ?? CancellationToken.None;
                        _ = UploadFrameAsync(frame, token);
                    }
                }
            }
            finally
            {
                finalBitmap?.Recycle();
                finalBitmap?.Dispose();
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ConsentCapture", "Frame processing failed: " + ex.Message);
        }
        finally
        {
            // CRITICAL: Always close the image to release buffer back to BufferQueue!
            image?.Close();
            image?.Dispose();
        }
    }

    private async Task PollingAndAutoCaptureLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2500, token);
                if (token.IsCancellationRequested) break;

                var now = DateTimeOffset.UtcNow;
                bool isAutoTime;
                lock (frameLock)
                {
                    isAutoTime = (now - lastUploadTime).TotalSeconds >= 30;
                }

                // Check if someone clicked "Take Snapshot" in the web portal
                bool isSnapshotRequested = await CheckPortalSnapshotRequestAsync(token);

                if (isSnapshotRequested || isAutoTime)
                {
                    byte[]? frameToUpload;
                    lock (frameLock)
                    {
                        frameToUpload = latestJpegBytes;
                    }

                    if (frameToUpload != null && frameToUpload.Length > 0)
                    {
                        lock (frameLock)
                        {
                            lastUploadTime = now;
                        }
                        await UploadFrameAsync(frameToUpload, token);
                    }
                }
            }
            catch (System.OperationCanceledException) { break; }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("ConsentCapture", "Polling loop exception: " + ex.Message);
            }
        }
    }

    private async Task<bool> CheckPortalSnapshotRequestAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(portalUrl) || string.IsNullOrWhiteSpace(accessToken)) return false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var endpoint = new Uri(portalUrl.TrimEnd('/') + "/api/snapshot-request");
            using var response = await client.GetAsync(endpoint, token);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(token);
                return json.Contains("\"requested\":true") || json.Contains("\"requested\": true");
            }
        }
        catch
        {
            // Fail silently if endpoint is unreachable or 404
        }
        return false;
    }

    private async Task UploadFrameAsync(byte[] frame, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        lock (frameLock)
        {
            if (isUploading) return;
            isUploading = true;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var form = new MultipartFormDataContent();
            using var content = new ByteArrayContent(frame);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
            form.Add(content, "image", "screen-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".jpg");
            var endpoint = new Uri(portalUrl.TrimEnd('/') + "/api/images");
            using var response = await client.PostAsync(endpoint, form, token);
            var status = response.IsSuccessStatusCode
                ? "Sharing active. Live screen updated at " + DateTime.Now.ToString("HH:mm:ss") + "."
                : "Sharing active. Portal returned HTTP " + (int)response.StatusCode + ".";
            SetServiceState(true, status);
            UpdateNotification(status);
        }
        catch (System.OperationCanceledException) { }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ConsentCapture", "Frame upload failed: " + ex);
            var status = "Sharing active. Upload issue: " + ex.Message;
            SetServiceState(true, status);
            UpdateNotification(status);
        }
        finally
        {
            lock (frameLock)
            {
                isUploading = false;
            }
        }
    }

    private Notification BuildNotification(string text)
    {
        var stopIntent = new Intent(this, typeof(ScreenCaptureService)).SetAction(ActionStop);
        var stopPending = PendingIntent.GetService(this, 91, stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var openIntent = new Intent(this, typeof(MainActivity));
        var openPending = PendingIntent.GetActivity(this, 92, openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        return builder.SetSmallIcon(global::Android.Resource.Drawable.IcMenuView)
            .SetContentTitle("Calculator")
            .SetContentText(text)
            .SetContentIntent(openPending)
            .SetOngoing(true)
            .SetCategory(Notification.CategoryService)
            .AddAction(new Notification.Action.Builder(null, "Stop sharing", stopPending).Build())
            .Build();
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Active screen sharing", NotificationImportance.Low)
        {
            Description = "Visible status and stop control while screen sharing is active."
        });
    }

    private void UpdateNotification(string text)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(text));
    }

    private void SetServiceState(bool active, string status)
    {
        IsRunning = active;
        GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.Edit()!
            .PutBoolean("captureActive", active)!
            .PutString("captureStatus", status)!
            .Apply();
    }

    private void StopCapture(bool stopProjection = true)
    {
        cancellation?.Cancel();
        cancellation?.Dispose(); cancellation = null;
        virtualDisplay?.Release(); virtualDisplay?.Dispose(); virtualDisplay = null;
        imageReader?.SetOnImageAvailableListener(null, null);
        imageReader?.Close(); imageReader?.Dispose(); imageReader = null;
        imageThread?.QuitSafely(); imageThread?.Dispose(); imageThread = null;
        if (stopProjection) projection?.Stop();
        projection?.Dispose(); projection = null; projectionCallback = null;
        if (wakeLock is not null)
        {
            if (wakeLock.IsHeld) wakeLock.Release();
            wakeLock.Dispose();
            wakeLock = null;
        }
        lock (frameLock)
        {
            lastUploadTime = DateTimeOffset.MinValue;
            latestJpegBytes = null;
            isUploading = false;
        }
        StopForeground(StopForegroundFlags.Remove);
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        base.OnTaskRemoved(rootIntent);
        // Explicitly maintain foreground capture service active even if app is removed from Recents
    }

    public override void OnDestroy()
    {
        StopCapture();
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private sealed class FrameListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly WeakReference<ScreenCaptureService> service;
        public FrameListener(ScreenCaptureService service) => this.service = new WeakReference<ScreenCaptureService>(service);
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null && service.TryGetTarget(out var target)) target.OnFrameAvailable(reader);
        }
    }

    private sealed class ProjectionCallback : MediaProjection.Callback
    {
        private readonly WeakReference<ScreenCaptureService> service;
        public ProjectionCallback(ScreenCaptureService service) => this.service = new WeakReference<ScreenCaptureService>(service);
        public override void OnStop()
        {
            if (service.TryGetTarget(out var target))
            {
                target.StopCapture(stopProjection: false);
                target.SetServiceState(false, "Screen sharing ended by Android.");
                target.StopSelf();
            }
        }
    }
}
