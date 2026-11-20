package nz.work.minecraft.jay;

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
}
