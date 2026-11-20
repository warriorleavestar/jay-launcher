package org.libsdl.app;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.LocaleList;
import android.view.Surface;
import android.widget.Toast;

/**
 * Small ART-side host for SDL3 when Minecraft runs in Jay's embedded HotSpot VM.
 *
 * SDL registers these native entry points when libSDL3 is first loaded by ART. HotSpot later
 * opens the same absolute library, so SDL keeps the Android VM, Activity and Surface references
 * it needs while the game remains isolated from launcher UI code.
 */
public final class SDLActivity {
    private static Activity activity;
    private static Surface surface;

    private SDLActivity() {}

    public static synchronized void initialize(Activity owner, Surface value,
                                                int width, int height, float density, float refreshRate) {
        activity = owner;
        surface = value;
        nativeSetupJNI();
        nativeSetScreenResolution(width, height, width, height, density, refreshRate);
        onNativeSurfaceCreated();
        onNativeSurfaceChanged();
        nativeFocusChanged(true);
    }

    public static synchronized void updateSurface(Surface value, int width, int height,
                                                  float density, float refreshRate) {
        surface = value;
        nativeSetScreenResolution(width, height, width, height, density, refreshRate);
        onNativeResize();
        onNativeSurfaceChanged();
    }

    public static synchronized void clearSurface() {
        onNativeSurfaceDestroyed();
        surface = null;
    }

    public static String clipboardGetText() {
        ClipboardManager manager = clipboard();
        if (manager == null || !manager.hasPrimaryClip()) return "";
        ClipData clip = manager.getPrimaryClip();
        if (clip == null || clip.getItemCount() == 0) return "";
        CharSequence text = clip.getItemAt(0).coerceToText(activity);
        return text == null ? "" : text.toString();
    }

    public static boolean clipboardHasText() {
        ClipboardManager manager = clipboard();
        return manager != null && manager.hasPrimaryClip();
    }

    public static void clipboardSetText(String text) {
        ClipboardManager manager = clipboard();
        if (manager != null) manager.setPrimaryClip(ClipData.newPlainText("", text));
    }

    private static ClipboardManager clipboard() {
        return activity == null ? null
            : (ClipboardManager)activity.getSystemService(Context.CLIPBOARD_SERVICE);
    }

    public static int createCustomCursor(int[] colors, int width, int height, int hotX, int hotY) { return 0; }
    public static void destroyCustomCursor(int cursor) {}
    public static Activity getContext() { return activity; }
    public static boolean getManifestEnvironmentVariables() { return true; }
    public static Surface getNativeSurface() { return surface; }
    public static void initTouch() {}
    public static boolean isAndroidTV() { return false; }
    public static boolean isChromebook() { return false; }
    public static boolean isDeXMode() { return false; }
    public static boolean isTablet() {
        return activity != null && activity.getResources().getConfiguration().smallestScreenWidthDp >= 600;
    }
    public static void manualBackButton() { if (activity != null) activity.onBackPressed(); }
    public static void minimizeWindow() { if (activity != null) activity.moveTaskToBack(true); }
    public static boolean openURL(String url) {
        if (activity == null) return false;
        try {
            Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(url));
            activity.startActivity(intent);
            return true;
        } catch (RuntimeException error) {
            return false;
        }
    }
    public static void requestPermission(String permission, int requestCode) {
        if (activity != null) activity.requestPermissions(new String[] { permission }, requestCode);
    }
    public static boolean showToast(String message, int duration, int gravity, int x, int y) {
        if (activity == null) return false;
        activity.runOnUiThread(() -> {
            Toast toast = Toast.makeText(activity, message,
                duration > 0 ? Toast.LENGTH_LONG : Toast.LENGTH_SHORT);
            toast.setGravity(gravity, x, y);
            toast.show();
        });
        return true;
    }
    public static boolean sendMessage(int command, int parameter) { return false; }
    public static boolean setActivityTitle(String title) {
        if (activity == null) return false;
        activity.runOnUiThread(() -> activity.setTitle(title));
        return true;
    }
    public static boolean setCustomCursor(int cursor) { return false; }
    public static void setOrientation(int width, int height, boolean resizable, String hint) {
        // GameActivity owns orientation through its SensorLandscape manifest setting. SDL calls
        // this while Minecraft creates short-lived utility windows whose dimensions do not
        // describe the Android display. Applying those dimensions here can flip to portrait,
        // recreate the Surface/Activity after HotSpot has started, and leave the JVM alive with
        // an EGL_BAD_SURFACE and no visible task.
    }
    public static boolean setRelativeMouseEnabled(boolean enabled) { return false; }
    public static boolean setSystemCursor(int cursor) { return false; }
    public static void setWindowStyle(boolean fullscreen) {}
    public static boolean shouldMinimizeOnFocusLoss() { return false; }
    public static boolean showTextInput(int x, int y, int width, int height, int inputType) { return false; }
    public static boolean supportsRelativeMouse() { return false; }
    public static int openFileDescriptor(String uri, String mode) { return -1; }
    public static boolean showFileDialog(String[] filters, boolean multi, boolean directories, int requestCode) {
        return false;
    }
    public static String getPreferredLocales() {
        LocaleList locales = LocaleList.getDefault();
        return locales.isEmpty() ? "" : locales.get(0).toLanguageTag();
    }

    public static native String nativeGetVersion();
    public static native void nativeSetupJNI();
    public static native void nativeInitMainThread();
    public static native void nativeCleanupMainThread();
    public static native int nativeRunMain(String library, String function, Object arguments);
    public static native void onNativeDropFile(String file);
    public static native void nativeSetScreenResolution(int surfaceWidth, int surfaceHeight,
                                                        int deviceWidth, int deviceHeight,
                                                        float density, float refreshRate);
    public static native void onNativeResize();
    public static native void onNativeSurfaceCreated();
    public static native void onNativeSurfaceChanged();
    public static native void onNativeSurfaceDestroyed();
    public static native void onNativeScreenKeyboardShown();
    public static native void onNativeScreenKeyboardHidden();
    public static native void onNativeKeyDown(int keyCode);
    public static native void onNativeKeyUp(int keyCode);
    public static native boolean onNativeSoftReturnKey();
    public static native void onNativeKeyboardFocusLost();
    public static native void onNativeTouch(int device, int pointer, int action,
                                            float x, float y, float pressure);
    public static native void onNativePinchStart();
    public static native void onNativePinchUpdate(float scale);
    public static native void onNativePinchEnd();
    public static native void onNativeMouse(int button, int action, float x, float y, boolean relative);
    public static native void onNativePen(int pen, int device, int button, int action,
                                          float x, float y, float pressure);
    public static native void onNativeAccel(float x, float y, float z);
    public static native void onNativeClipboardChanged();
    public static native void nativeLowMemory();
    public static native void onNativeLocaleChanged();
    public static native void onNativeDarkModeChanged(boolean enabled);
    public static native void nativeSendQuit();
    public static native void nativeQuit();
    public static native void nativePause();
    public static native void nativeResume();
    public static native void nativeFocusChanged(boolean focused);
    public static native String nativeGetHint(String name);
    public static native boolean nativeGetHintBoolean(String name, boolean fallback);
    public static native void nativeSetenv(String name, String value);
    public static native void nativeSetNaturalOrientation(int orientation);
    public static native void onNativeRotationChanged(int rotation);
    public static native void onNativeInsetsChanged(int left, int right, int top, int bottom);
    public static native void nativeAddTouch(int id, String name);
    public static native void nativePermissionResult(int requestCode, boolean granted);
    public static native boolean nativeAllowRecreateActivity();
    public static native int nativeCheckSDLThreadCounter();
    public static native void onNativeFileDialog(int requestCode, String[] files, int filter);
}

final class SDLInputConnection {
    private SDLInputConnection() {}
    public static native void nativeCommitText(String text, int cursor);
    public static native void nativeGenerateScancodeForUnichar(char value);
}

final class SDLAudioManager {
    private SDLAudioManager() {}
    public static native void nativeSetupJNI();
    public static native void nativeAddAudioDevice(boolean recording, String name, int id);
    public static native void nativeRemoveAudioDevice(boolean recording, int id);
}

final class SDLControllerManager {
    private SDLControllerManager() {}
    public static native void nativeSetupJNI();
    public static native boolean onNativePadDown(int device, int keyCode, int scanCode);
    public static native boolean onNativePadUp(int device, int keyCode, int scanCode);
    public static native void onNativeJoy(int device, int axis, float value);
    public static native void onNativeHat(int device, int hat, int x, int y);
    public static native void onNativeJoySensor(int device, int sensor, long timestamp,
                                                float x, float y, float z);
    public static native void nativeAddJoystick(int id, String name, String description,
                                                int vendor, int product, int buttons, int axes,
                                                int axisMask, int hats, boolean rumble,
                                                boolean rgb, boolean accelerometer, boolean gyroscope);
    public static native void nativeRemoveJoystick(int id);
    public static native void nativeAddHaptic(int id, String name);
    public static native void nativeRemoveHaptic(int id);
}

final class HIDDeviceManager {
    private HIDDeviceManager() {}
    public static native void HIDDeviceRegisterCallback();
    public static native void HIDDeviceReleaseCallback();
    public static native void HIDDeviceConnected(int id, String identifier, int vendor, int product,
                                                 String serial, int release, String manufacturer,
                                                 String name, int deviceInterface, int interfaceClass,
                                                 int interfaceSubclass, int interfaceProtocol,
                                                 boolean bluetooth, int reportId);
    public static native void HIDDeviceOpenPending(int id);
    public static native void HIDDeviceOpenResult(int id, boolean opened);
    public static native void HIDDeviceDisconnected(int id);
    public static native void HIDDeviceInputReport(int id, byte[] value);
    public static native void HIDDeviceReportResponse(int id, byte[] value);
}
