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
    public const string ActionStart = "com.companyname.ConsentCapture.START";
    public const string ActionStop = "com.companyname.ConsentCapture.STOP";
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
    private DateTimeOffset lastFrameAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastFrameTime = DateTimeOffset.MinValue;

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
        if (intent?.Action != ActionStart)
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
            SetServiceState(false, "Screen sharing was not authorized or the portal token is missing.");
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        try
        {
            var notification = BuildNotification("Starting screen sharing. Uploading a frame every 10 seconds.");
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
            new Handler(Looper.MainLooper!).PostDelayed(() =>
            {
                if (projection is null) return;
                lock (frameLock)
                {
                    if (lastFrameTime != DateTimeOffset.MinValue) return;
                }
                var message = "Capture session is active, but Android has not delivered a screen frame yet.";
                SetServiceState(true, message);
                UpdateNotification(message);
            }, 15000);
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
        // Half-resolution keeps the image reader and encoder light on memory-heavy phones.
        var width = Math.Max(1, metrics.WidthPixels / 2);
        var height = Math.Max(1, metrics.HeightPixels / 2);
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
        try
        {
            using var image = reader.AcquireLatestImage();
            if (image is null) return;
            var now = DateTimeOffset.UtcNow;
            lock (frameLock)
            {
                if (now - lastFrameAttemptAt < TimeSpan.FromSeconds(10)) return;
                lastFrameAttemptAt = now;
            }
            var plane = image.GetPlanes()?[0];
            if (plane is null) return;
            var width = image.Width;
            var height = image.Height;
            var pixelStride = plane.PixelStride;
            var rowStride = plane.RowStride;
            var source = plane.Buffer;
            var bytes = new byte[source.Remaining()];
            source.Get(bytes);

            var colors = new int[checked(width * height)];
            for (var y = 0; y < height; y++)
            {
                var rowStart = y * rowStride;
                for (var x = 0; x < width; x++)
                {
                    var offset = rowStart + x * pixelStride;
                    if (offset + 3 >= bytes.Length) continue;
                    colors[y * width + x] = (bytes[offset + 3] << 24) | (bytes[offset] << 16) |
                                            (bytes[offset + 1] << 8) | bytes[offset + 2];
                }
            }
            using var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
            bitmap.SetPixels(colors, 0, width, 0, 0, width, height);
            using var output = new MemoryStream();
            if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 78, output)) return;
            var frame = output.ToArray();
            lock (frameLock) lastFrameTime = now;
            var token = cancellation?.Token ?? CancellationToken.None;
            _ = UploadFrameAsync(frame, token);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("ConsentCapture", "Frame conversion failed: " + ex.Message);
            var message = "Screen capture failed: " + ex.Message;
            SetServiceState(true, message);
            UpdateNotification(message);
        }
    }

    private async Task UploadFrameAsync(byte[] frame, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var form = new MultipartFormDataContent();
            using var content = new ByteArrayContent(frame);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
            form.Add(content, "image", "screen-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".jpg");
            var endpoint = new Uri(portalUrl.TrimEnd('/') + "/api/images");
            using var response = await client.PostAsync(endpoint, form, token);
            var status = response.IsSuccessStatusCode
                ? "Sharing is active. Last frame uploaded at " + DateTime.Now.ToString("HH:mm:ss") + "."
                : "Sharing is active. Portal returned HTTP " + (int)response.StatusCode + ".";
            SetServiceState(true, status);
            UpdateNotification(status);
        }
        catch (System.OperationCanceledException) { }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("ConsentCapture", "Frame upload failed: " + ex);
            var status = "Sharing is active. Upload issue: " + ex.Message;
            SetServiceState(true, status);
            UpdateNotification(status);
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
            lastFrameAttemptAt = DateTimeOffset.MinValue;
            lastFrameTime = DateTimeOffset.MinValue;
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
