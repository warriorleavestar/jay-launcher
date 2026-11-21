package nz.work.minecraft.jay;

import android.view.KeyEvent;
import org.libsdl.app.SDLActivity;

/** SDL owns the Android surface and delivers touch events on its activity lifecycle. */
public final class JaySdlActivity extends SDLActivity {
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

    @Override public void onBackPressed() {
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
}
