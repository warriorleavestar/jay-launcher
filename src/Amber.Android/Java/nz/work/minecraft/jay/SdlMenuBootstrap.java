package nz.work.minecraft.jay;

import java.lang.reflect.*;
import java.util.Arrays;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

/** Deliver touchscreen menu gestures on modern SDL Minecraft's client thread in scaled GUI coordinates. */
public final class SdlMenuBootstrap {
    private static final int MOVE_ACTION = 0;
    private static final int DOWN_ACTION = 1;
    private static final int UP_ACTION = 2;
    private static final int GAMEPLAY_CONTEXTUAL_ACTION = 3;
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
        final Method hitType = hitResult.getType().getMethod("getType");
        final Object blockType = hitType.getReturnType().getField("BLOCK").get(null);
        final Method attack = client.getDeclaredMethod("startAttack");
        final Method use = client.getDeclaredMethod("startUseItem");
        final Class<?> container = Class.forName("net.minecraft.client.gui.screens.inventory.AbstractContainerScreen");
        final Class<?> slot = Class.forName("net.minecraft.world.inventory.Slot");
        final Class<?> clickType = Class.forName("net.minecraft.world.inventory.ContainerInput");
        final Method findSlot = container.getDeclaredMethod("getHoveredSlot", double.class, double.class);
        final Method slotClicked = container.getDeclaredMethod("slotClicked", slot, int.class, int.class, clickType);
        final Field slotIndex = slot.getField("index");
        final Method slotItem = slot.getMethod("getItem");
        final Method itemCount = Class.forName("net.minecraft.world.item.ItemStack").getMethod("getCount");
        final Object pickupAll = clickType.getField("PICKUP_ALL").get(null);
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
            findSlot.setAccessible(true);
            slotClicked.setAccessible(true);
        }

        // Minecraft also represents a ray that missed as a BlockHitResult.
        boolean targetsBlock(Object hit) throws ReflectiveOperationException {
            return hit != null && hitType.invoke(hit) == blockType;
        }

        int stackCount(Object game, float nx, float ny) throws ReflectiveOperationException {
            Object active = current.invoke(guiField.get(game));
            if (!container.isInstance(active)) return -1;
            Object hovered = findSlot.invoke(active, nx * width.getInt(active), ny * height.getInt(active));
            if (hovered == null) return -1;
            int index = slotIndex.getInt(hovered);
            if (index < 0 || index > 0x7ffffe) return -1;
            Object stack = slotItem.invoke(hovered);
            int count = stack == null ? 0 : Math.max(0, (Integer)itemCount.invoke(stack));
            return ((index + 1) << 8) | Math.min(255, count);
        }

        boolean split(Object game, float nx, float ny, int requested) throws ReflectiveOperationException {
            Object active = current.invoke(guiField.get(game));
            if (!container.isInstance(active)) return false;
            Object hovered = findSlot.invoke(active, nx * width.getInt(active), ny * height.getInt(active));
            if (hovered == null) return false;
            Object stack = slotItem.invoke(hovered);
            int total = stack == null ? 0 : Math.max(0, (Integer)itemCount.invoke(stack));
            int index = slotIndex.getInt(hovered);
            if (requested == -1) {
                slotClicked.invoke(active, hovered, index, 1, pickup);
                return true;
            }
            if (requested == 0) {
                slotClicked.invoke(active, hovered, index, 0, pickupAll);
                return true;
            }
            if (total == 0) return false;
            int selected = Math.max(1, Math.min(total, requested));
            int half = (total + 1) / 2;
            int button = selected <= half ? 1 : 0;
            slotClicked.invoke(active, hovered, index, button, pickup);
            for (int remaining = button == 1 ? half : total; remaining > selected; remaining--)
                slotClicked.invoke(active, hovered, index, 1, pickup);
            return true;
        }

        void deliver(Object game, float nx, float ny, int action) throws ReflectiveOperationException {
            Object active = current.invoke(guiField.get(game));
            if (action == GAMEPLAY_CONTEXTUAL_ACTION) {
                if (active != null) return;
                Object hit = hitResult.get(game);
                if (targetsBlock(hit)) use.invoke(game);
                else attack.invoke(game);
                return;
            }
            if (active != previousScreen) held = false;
            previousScreen = active;
            if (active == null) return;
            double x = nx * width.getInt(active), y = ny * height.getInt(active);
            Object click = makeEvent.newInstance(x, y, left);
            if (action == DOWN_ACTION) {
                down.invoke(active, click, false);
                held = true;
            } else if (action == UP_ACTION) {
                if (held) up.invoke(active, click);
                held = false;
            } else if (action == MOVE_ACTION) {
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
            if (!(game instanceof Executor)) return -1;
            FutureTask<Integer> query = new FutureTask<>(() -> {
                try { return input.stackCount(game, x, y); }
                catch (ReflectiveOperationException error) {
                    System.err.println("Jay inventory query: " + error);
                    return -1;
                }
            });
            ((Executor)game).execute(query);
            try {
                return query.get(250, TimeUnit.MILLISECONDS);
            } catch (InterruptedException interrupted) {
                query.cancel(false);
                Thread.currentThread().interrupt();
                return -1;
            } catch (ExecutionException | TimeoutException unavailable) {
                query.cancel(false);
                System.err.println("Jay inventory query: " + unavailable);
                return -1;
            }
        } catch (ReflectiveOperationException error) {
            return -1;
        }
    }

    public static int miningProgress() { return AndroidGameBootstrap.miningProgress(); }
    public static boolean requestPause() { return AndroidGameBootstrap.requestPause(); }

    public static int gameplayControlsState() { return AndroidGameBootstrap.gameplayControlsState(); }

    /** Current Minecraft GUI scale, cached while transitioning from a menu into gameplay. */
    public static int guiScale() {
        return AndroidGameBootstrap.guiScale();
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

    /** True only while ordinary gameplay is aiming at a block that can be mined. */
    public static boolean isBlockTarget() {
        try {
            if (menu == null) menu = new Menu();
            Menu input = menu;
            Object game = input.instance.invoke(null);
            if (game == null) return false;
            Object active = input.current.invoke(input.guiField.get(game));
            Object hit = input.hitResult.get(game);
            return active == null && input.targetsBlock(hit);
        } catch (ReflectiveOperationException | RuntimeException error) {
            return false;
        }
    }

    /** Keep the SDL/Java 25 menu path on the same editor-focus bridge as GLFW. */
    public static int textInputFocusState() {
        return AndroidGameBootstrap.textInputFocusState();
    }

    /** True only when this touch began over the focused Minecraft editor. */
    public static boolean isTextInputAt(float x, float y) {
        return AndroidGameBootstrap.isTextInputAt(x, y);
    }

    // SDLActivity owns these event types. The native host probes one common bridge surface for
    // both GLFW and SDL, so explicit no-op methods avoid treating the intentional SDL routing as
    // a missing-method error during startup.
    public static boolean dispatchMouseButton(int button, int action) { return false; }
    public static boolean dispatchKey(int key, int action) { return false; }
    public static boolean dispatchScroll(double x, double y) { return false; }
    public static boolean dispatchChar(int codepoint) { return false; }
    public static boolean dispatchLook(float x, float y) { return false; }

    public static void main(String[] args) throws Exception {
        AndroidGameBootstrap.routeConsoleToLogcat();
        Class.forName(args[0]).getMethod("main", String[].class)
            .invoke(null, (Object)Arrays.copyOfRange(args, 1, args.length));
    }
}
