package nz.work.minecraft.jay;

import java.lang.reflect.*;
import java.util.Arrays;
import java.util.concurrent.Executor;

/** Deliver touchscreen menu gestures on the 26.3 client thread in scaled GUI coordinates. */
public final class SdlMenuBootstrap {
    private static volatile Menu menu;
    private static boolean held;
    private static Object previousScreen;
    private static double previousX, previousY;

    private static final class Menu {
        final Class<?> client = Class.forName("net.minecraft.client.Minecraft");
        final Class<?> gui = Class.forName("net.minecraft.client.gui.Gui");
        final Class<?> screen = Class.forName("net.minecraft.client.gui.screens.Screen");
        final Class<?> listener = Class.forName("net.minecraft.client.gui.components.events.GuiEventListener");
        final Class<?> info = Class.forName("net.minecraft.client.input.MouseButtonInfo");
        final Class<?> event = Class.forName("net.minecraft.client.input.MouseButtonEvent");
        final Method instance = client.getMethod("getInstance");
        final Field guiField = client.getField("gui");
        final Method current = gui.getMethod("screen");
        final Field width = screen.getField("width"), height = screen.getField("height");
        final Constructor<?> makeEvent = event.getConstructor(double.class, double.class, info);
        final Object left = info.getConstructor(int.class, int.class).newInstance(1, 0);
        final Method move = listener.getMethod("mouseMoved", double.class, double.class);
        final Method down = listener.getMethod("mouseClicked", event, boolean.class);
        final Method up = listener.getMethod("mouseReleased", event);
        final Method drag = listener.getMethod("mouseDragged", event, double.class, double.class);

        Menu() throws ReflectiveOperationException {}

        void deliver(Object game, float nx, float ny, int action) throws ReflectiveOperationException {
            Object active = current.invoke(guiField.get(game));
            if (active != previousScreen) held = false;
            previousScreen = active;
            if (active == null) return;
            double x = nx * width.getInt(active), y = ny * height.getInt(active);
            Object click = makeEvent.newInstance(x, y, left);
            if (action == 1) {
                down.invoke(active, click, false);
                held = true;
            } else if (action == 2) {
                if (held) up.invoke(active, click);
                held = false;
            } else {
                move.invoke(active, x, y);
                if (held) drag.invoke(active, click, x - previousX, y - previousY);
            }
            previousX = x;
            previousY = y;
        }
    }

    public static boolean dispatchPointer(float x, float y, int action) {
        try {
            if (menu == null) menu = new Menu();
            Menu input = menu;
            Object game = input.instance.invoke(null);
            if (!(game instanceof Executor)) return false;
            ((Executor)game).execute(() -> {
                try { input.deliver(game, x, y, action); }
                catch (ReflectiveOperationException error) { System.err.println("Jay menu input: " + error); }
            });
            return true;
        } catch (ReflectiveOperationException error) {
            return false;
        }
    }

    public static void main(String[] args) throws Exception {
        Class.forName(args[0]).getMethod("main", String[].class)
            .invoke(null, (Object)Arrays.copyOfRange(args, 1, args.length));
    }
}
