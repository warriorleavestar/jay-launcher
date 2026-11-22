package nz.work.minecraft.jay;

import android.os.Build;
import android.os.Bundle;
import android.view.KeyEvent;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;
import org.libsdl.app.SDLActivity;

/** SDL owns the Android surface and delivers touch events on its activity lifecycle. */
public final class JaySdlActivity extends SDLActivity {
    private Object jayBackInvokedCallback;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (Build.VERSION.SDK_INT >= 33) {
            jayBackInvokedCallback = Api33BackHandler.register(this);
        }
    }

    @Override protected String[] getLibraries() {
        return new String[] { "SDL3" };
    }

    @Override protected void main() {
        try {
            // The managed callable wrapper is generated after AndroidJavaSource is compiled.
            Class.forName("nz.work.minecraft.jay.SdlEntry").getMethod("startGame", android.app.Activity.class).invoke(null, this);
        } catch (ReflectiveOperationException error) {
            throw new IllegalStateException("Could not invoke the managed game host.", error);
        }
    }

    private void handleSystemBack() {
        // SDLActivity's default implementation finishes the Activity. In Jay that also tears
        // down the isolated HotSpot process, so an Android back gesture could kill a world while
        // it was still saving. Once the SDL game thread exists, treat Back exactly like the
        // on-screen menu button and let Minecraft's normal Escape flow pause/close the screen.
        if (mSDLThread != null && !mSDLMainFinished) {
            onNativeKeyDown(KeyEvent.KEYCODE_ESCAPE);
            onNativeKeyUp(KeyEvent.KEYCODE_ESCAPE);
            return;
        }
        super.onBackPressed();
    }

    @Override public void onBackPressed() {
        handleSystemBack();
    }

    @Override protected void onDestroy() {
        if (Build.VERSION.SDK_INT >= 33 && jayBackInvokedCallback != null) {
            Api33BackHandler.unregister(this, jayBackInvokedCallback);
            jayBackInvokedCallback = null;
        }
        super.onDestroy();
    }

    /** Keep API 33 types out of the activity's fields so this class remains loadable on old Android. */
    private static final class Api33BackHandler {
        private static Object register(final JaySdlActivity activity) {
            OnBackInvokedCallback callback = new OnBackInvokedCallback() {
                @Override public void onBackInvoked() {
                    activity.handleSystemBack();
                }
            };
            activity.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    OnBackInvokedDispatcher.PRIORITY_DEFAULT, callback);
            return callback;
        }

        private static void unregister(JaySdlActivity activity, Object callback) {
            activity.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(
                    (OnBackInvokedCallback) callback);
        }
    }
}
