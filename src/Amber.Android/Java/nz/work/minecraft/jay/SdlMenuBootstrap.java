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
        final Field hitResult = client.getField("hitResult");
        final Class<?> blockHit = Class.forName("net.minecraft.world.phys.BlockHitResult");
        final Method attack = client.getDeclaredMethod("startAttack");
        final Method use = client.getDeclaredMethod("startUseItem");
        final Class<?> container = Class.forName("net.minecraft.client.gui.screens.inventory.AbstractContainerScreen");
        final Class<?> slot = Class.forName("net.minecraft.world.inventory.Slot");
        final Class<?> clickType = Class.forName("net.minecraft.world.inventory.ContainerInput");
        final Field hoveredSlot = container.getDeclaredField("hoveredSlot");
        final Method slotClicked = container.getDeclaredMethod("slotClicked", slot, int.class, int.class, clickType);
        final Field slotIndex = slot.getField("index");
        final Method slotItem = slot.getMethod("getItem");
        final Method itemCount = Class.forName("net.minecraft.world.item.ItemStack").getMethod("getCount");
        final Object pickup = clickType.getField("PICKUP").get(null);
        final Field width = screen.getField("width"), height = screen.getField("height");
        final Constructor<?> makeEvent = event.getConstructor(double.class, double.class, info);
        final Object left = info.getConstructor(int.class, int.class).newInstance(1, 0);
        final Method move = listener.getMethod("mouseMoved", double.class, double.class);
        final Method down = listener.getMethod("mouseClicked", event, boolean.class);
        final Method up = listener.getMethod("mouseReleased", event);
        final Method drag = listener.getMethod("mouseDragged", event, double.class, double.class);

        Menu() throws ReflectiveOperationException {
            attack.setAccessible(true);
            use.setAccessible(true);
            hoveredSlot.setAccessible(true);
            slotClicked.setAccessible(true);
        }

        int stackCount(Object game, float nx, float ny) throws ReflectiveOperationException {
            Object active = current.invoke(guiField.get(game));
            if (!container.isInstance(active)) return -1;
            Object hovered = hoveredSlot.get(active);
            if (hovered == null) return -1;
            Object stack = slotItem.invoke(hovered);
            return stack == null ? 0 : Math.max(0, (Integer)itemCount.invoke(stack));
        }

        boolean split(Object game, float nx, float ny, int requested) throws ReflectiveOperationException {
            Object active = current.invoke(guiField.get(game));
            if (!container.isInstance(active)) return false;
            Object hovered = hoveredSlot.get(active);
            if (hovered == null) return false;
            Object stack = slotItem.invoke(hovered);
            int total = stack == null ? 0 : Math.max(0, (Integer)itemCount.invoke(stack));
            if (total == 0) return false;
            int selected = Math.max(1, Math.min(total, requested));
            int index = slotIndex.getInt(hovered);
            slotClicked.invoke(active, hovered, index, 0, pickup);
            for (int remaining = total; remaining > selected; remaining--)
                slotClicked.invoke(active, hovered, index, 1, pickup);
            return true;
        }

        void deliver(Object game, float nx, float ny, int action) throws ReflectiveOperationException {
            Object active = current.invoke(guiField.get(game));
            if (action == 3) {
                if (active != null) return;
                Object hit = hitResult.get(game);
                if (hit != null && blockHit.isInstance(hit)) use.invoke(game);
                else attack.invoke(game);
                return;
            }
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

    public static int inventoryStackCount(float x, float y) {
        try {
            if (menu == null) menu = new Menu();
            Menu input = menu;
            Object game = input.instance.invoke(null);
            return game == null ? -1 : input.stackCount(game, x, y);
        } catch (ReflectiveOperationException error) {
            return -1;
        }
    }

    public static boolean dispatchInventorySplit(float x, float y, int count) {
        try {
            if (menu == null) menu = new Menu();
            Menu input = menu;
            Object game = input.instance.invoke(null);
            if (!(game instanceof Executor)) return false;
            ((Executor)game).execute(() -> {
                try { input.split(game, x, y, count); }
                catch (ReflectiveOperationException error) { System.err.println("Jay inventory input: " + error); }
            });
            return true;
        } catch (ReflectiveOperationException error) {
            return false;
        }
    }

    public static boolean isBlockTarget() { return false; }

    // SDLActivity owns these event types. The native host probes one common bridge surface for
    // both GLFW and SDL, so explicit no-op methods avoid treating the intentional SDL routing as
    // a missing-method error during startup.
    public static boolean dispatchMouseButton(int button, int action) { return false; }
    public static boolean dispatchKey(int key, int action) { return false; }
    public static boolean dispatchScroll(double x, double y) { return false; }
    public static boolean dispatchChar(int codepoint) { return false; }
    public static boolean dispatchLook(float x, float y) { return false; }

    public static void main(String[] args) throws Exception {
        Class.forName(args[0]).getMethod("main", String[].class)
            .invoke(null, (Object)Arrays.copyOfRange(args, 1, args.length));
    }
}
