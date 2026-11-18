package nz.work.minecraft.jay;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.lang.reflect.Field;
import java.lang.reflect.Constructor;
import java.nio.ByteBuffer;
import java.util.Arrays;

/** Supplies LWJGL OpenGL entry points through Jay's EGL-backed GLFW bridge before Minecraft starts. */
public final class AndroidGameBootstrap {
    private static Field minecraftInstance;
    private static Field minecraftMouse;
    private static Field minecraftKeyboard;
    private static Field minecraftWindow;
    private static Field minecraftScreen;
    private static Field minecraftHitResult;
    private static Field mouseX;
    private static Field mouseY;
    private static Method windowHandle;
    private static Method windowWidth;
    private static Method windowHeight;
    private static Method mouseMove;
    private static Method mouseButton;
    private static Method legacyScreenClick;
    private static Method legacyScreenRelease;
    private static Method legacyOverlay;
    private static Constructor<?> mouseInput;
    private static Method mouseScroll;
    private static Method keyboardKey;
    private static Method keyboardChar;
    private static Method startAttack;
    private static Method startUseItem;
    private static Class<?> blockHitType;
    private static Class<?> containerScreenType;
    private static Method containerHoveredSlot;
    private static Method containerSlotClicked;
    private static Field screenWidth;
    private static Field screenHeight;
    private static Field slotId;
    private static Method slotGetItem;
    private static Field itemStackCount;
    private static Object pickupClickType;
    private static boolean inputBridgeReady;
    private static boolean inputBridgeFailed;
    private static boolean inputDispatchErrorLogged;

    private AndroidGameBootstrap() {}

    private static synchronized boolean prepareInputBridge() {
        if (inputBridgeReady) return true;
        if (inputBridgeFailed) return false;
        try {
            Class<?> minecraftType;
            boolean legacy;
            try {
                minecraftType = Class.forName("enn", false, AndroidGameBootstrap.class.getClassLoader());
                // Distinguish the vanilla 1.20.1 client from unrelated classes with the same short name.
                if (minecraftType.getDeclaredField("F").getType() != minecraftType)
                    throw new NoSuchFieldException("Minecraft.instance");
                legacy = true;
            } catch (ClassNotFoundException | NoSuchFieldException otherVersion) {
                minecraftType = Class.forName("gfj");
                legacy = false;
            }
            Class<?> mouseType = Class.forName(legacy ? "eno" : "gfk");
            Class<?> keyboardType = Class.forName(legacy ? "enm" : "gfi");
            Class<?> windowType = Class.forName(legacy ? "ehn" : "fyk");
            Class<?> mouseInputType = legacy ? null : Class.forName("gzd");

            minecraftInstance = accessible(minecraftType.getDeclaredField(legacy ? "F" : "A"));
            minecraftMouse = accessible(minecraftType.getDeclaredField("n"));
            minecraftKeyboard = accessible(minecraftType.getDeclaredField("o"));
            minecraftWindow = accessible(minecraftType.getDeclaredField(legacy ? "Q" : "O"));
            minecraftScreen = accessible(minecraftType.getDeclaredField(legacy ? "z" : "x"));
            minecraftHitResult = accessible(minecraftType.getDeclaredField(legacy ? "w" : "u"));
            mouseX = accessible(mouseType.getDeclaredField(legacy ? "e" : "h"));
            mouseY = accessible(mouseType.getDeclaredField(legacy ? "f" : "i"));
            windowHandle = accessible(windowType.getDeclaredMethod(legacy ? "i" : "h"));
            windowWidth = accessible(windowType.getDeclaredMethod("m"));
            windowHeight = accessible(windowType.getDeclaredMethod("n"));
            mouseMove = accessible(mouseType.getDeclaredMethod(
                "b", long.class, double.class, double.class));
            mouseButton = legacy ? accessible(mouseType.getDeclaredMethod("a", long.class, int.class, int.class, int.class)) : accessible(mouseType.getDeclaredMethod(
                "a", long.class, mouseInputType, int.class));
            mouseInput = legacy ? null : accessible(mouseInputType.getDeclaredConstructor(int.class, int.class));
            mouseScroll = accessible(mouseType.getDeclaredMethod(
                legacy ? "a" : "c", long.class, double.class, double.class));
            keyboardKey = accessible(keyboardType.getDeclaredMethod(
                "a", long.class, int.class, int.class, int.class, int.class));
            keyboardChar = accessible(keyboardType.getDeclaredMethod(
                "a", long.class, int.class, int.class));
            startAttack = accessible(minecraftType.getDeclaredMethod(legacy ? "bj" : "bu"));
            startUseItem = accessible(minecraftType.getDeclaredMethod(legacy ? "bs" : "bv"));
            blockHitType = Class.forName(legacy ? "eee" : "fti");
            Class<?> screenType = Class.forName(legacy ? "euq" : "gsb");
            if (legacy) {
                Class<?> listener = Class.forName("eqt");
                legacyScreenClick = listener.getMethod("a", double.class, double.class, int.class);
                legacyScreenRelease = listener.getMethod("b", double.class, double.class, int.class);
                legacyOverlay = minecraftType.getMethod("aJ");
            }
            containerScreenType = Class.forName(legacy ? "evp" : "gti");
            Class<?> slotType = Class.forName(legacy ? "ccx" : "dji");
            Class<?> itemStackType = Class.forName(legacy ? "cfz" : "dlt");
            Class<?> clickType = Class.forName(legacy ? "cbo" : "dhu");
            screenWidth = accessible(screenType.getDeclaredField(legacy ? "g" : "o"));
            screenHeight = accessible(screenType.getDeclaredField(legacy ? "h" : "p"));
            containerHoveredSlot = accessible(containerScreenType.getDeclaredMethod(
                legacy ? "a" : "b", double.class, double.class));
            containerSlotClicked = accessible(containerScreenType.getDeclaredMethod(
                "a", slotType, int.class, int.class, clickType));
            slotId = accessible(slotType.getDeclaredField(legacy ? "e" : "a"));
            slotGetItem = accessible(slotType.getDeclaredMethod(legacy ? "e" : "g"));
            itemStackCount = accessible(itemStackType.getDeclaredField(legacy ? "s" : "r"));
            pickupClickType = accessible(clickType.getDeclaredField("a")).get(null);
            inputBridgeReady = true;
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            inputBridgeFailed = true;
            System.err.println("Jay input bridge is unavailable: " + error);
            return false;
        }
    }

    private static <T extends java.lang.reflect.AccessibleObject> T accessible(T value) {
        value.setAccessible(true);
        return value;
    }

    private static Object minecraft() throws ReflectiveOperationException {
        return minecraftInstance.get(null);
    }

    private static Object window(Object minecraft) throws ReflectiveOperationException {
        return minecraftWindow.get(minecraft);
    }

    private static long handle(Object window) throws ReflectiveOperationException {
        return (Long)windowHandle.invoke(window);
    }

    public static boolean dispatchPointer(float normalizedX, float normalizedY, int clicks) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object window = window(minecraft);
            Object mouse = minecraftMouse.get(minecraft);
            if (window == null || mouse == null) return false;
            double x = Math.max(0.0, Math.min(1.0, normalizedX)) * (Integer)windowWidth.invoke(window);
            double y = Math.max(0.0, Math.min(1.0, normalizedY)) * (Integer)windowHeight.invoke(window);
            long handle = handle(window);
            // The native cursor callback is queued by Minecraft. Keep its state in sync now so a
            // press and release from the same Android gesture use the new position immediately.
            mouseX.setDouble(mouse, x);
            mouseY.setDouble(mouse, y);
            mouseMove.invoke(mouse, handle, x, y);
            for (int index = 0; index < clicks; index++) {
                Object screen = minecraftScreen.get(minecraft);
                if (screen == null) {
                    Object hitResult = minecraftHitResult.get(minecraft);
                    if (hitResult != null && blockHitType.isInstance(hitResult))
                        startUseItem.invoke(minecraft);
                    else
                        startAttack.invoke(minecraft);
                } else if (legacyScreenClick != null && !containerScreenType.isInstance(screen)
                        && legacyOverlay.invoke(minecraft) == null) {
                    // GUI events use scaled screen units, independently of framebuffer density.
                    double guiX = normalizedX * screenWidth.getInt(screen);
                    double guiY = normalizedY * screenHeight.getInt(screen);
                    legacyScreenClick.invoke(screen, guiX, guiY, 0);
                    if (minecraftScreen.get(minecraft) == screen)
                        legacyScreenRelease.invoke(screen, guiX, guiY, 0);
                } else {
                    pressMouse(mouse, handle, 0, 1);
                    pressMouse(mouse, handle, 0, 0);
                }
            }
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return reportInputError(error);
        }
    }

    /** Returns the live count under a normalized pointer, or -1 outside a container slot. */
    public static int inventoryStackCount(float normalizedX, float normalizedY) {
        if (!prepareInputBridge()) return -1;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return -1;
            Object screen = minecraftScreen.get(minecraft);
            if (!containerScreenType.isInstance(screen)) return -1;
            double x = Math.max(0.0, Math.min(1.0, normalizedX)) * screenWidth.getInt(screen);
            double y = Math.max(0.0, Math.min(1.0, normalizedY)) * screenHeight.getInt(screen);
            Object slot = containerHoveredSlot.invoke(screen, x, y);
            if (slot == null) return -1;
            Object stack = slotGetItem.invoke(slot);
            return stack == null ? 0 : Math.max(0, itemStackCount.getInt(stack));
        } catch (ReflectiveOperationException | RuntimeException error) {
            reportInputError(error);
            return -1;
        }
    }

    /** Picks an exact quantity from a slot using Minecraft's own synchronized click path. */
    public static boolean dispatchInventorySplit(float normalizedX, float normalizedY, int requested) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object screen = minecraftScreen.get(minecraft);
            if (!containerScreenType.isInstance(screen)) return false;
            double x = Math.max(0.0, Math.min(1.0, normalizedX)) * screenWidth.getInt(screen);
            double y = Math.max(0.0, Math.min(1.0, normalizedY)) * screenHeight.getInt(screen);
            Object slot = containerHoveredSlot.invoke(screen, x, y);
            if (slot == null) return false;
            Object stack = slotGetItem.invoke(slot);
            int total = stack == null ? 0 : Math.max(0, itemStackCount.getInt(stack));
            int selected = Math.max(1, Math.min(total, requested));
            if (total <= 0) return false;
            int id = slotId.getInt(slot);
            // Left pickup takes the whole stack. Each right pickup on the source returns one,
            // leaving precisely "selected" items on the cursor for the next finger tap.
            containerSlotClicked.invoke(screen, slot, id, 0, pickupClickType);
            for (int index = selected; index < total; index++)
                containerSlotClicked.invoke(screen, slot, id, 1, pickupClickType);
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return reportInputError(error);
        }
    }

    /** True only while normal gameplay is aiming at a block that can be mined. */
    public static boolean isBlockTarget() {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            return minecraft != null
                && minecraftScreen.get(minecraft) == null
                && blockHitType.isInstance(minecraftHitResult.get(minecraft));
        } catch (ReflectiveOperationException | RuntimeException error) {
            return false;
        }
    }

    private static void pressMouse(Object mouse, long window, int button, int action) throws ReflectiveOperationException {
        if (mouseInput == null) mouseButton.invoke(mouse, window, button, action, 0);
        else mouseButton.invoke(mouse, window, mouseInput.newInstance(button, 0), action);
    }

    public static boolean dispatchMouseButton(int button, int action) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object window = window(minecraft);
            Object mouse = minecraftMouse.get(minecraft);
            if (window == null || mouse == null) return false;
            pressMouse(mouse, handle(window), button, action);
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return reportInputError(error);
        }
    }

    public static boolean dispatchKey(int key, int action) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object window = window(minecraft);
            Object keyboard = minecraftKeyboard.get(minecraft);
            if (window == null || keyboard == null) return false;
            keyboardKey.invoke(keyboard, handle(window), key, key, action, 0);
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return reportInputError(error);
        }
    }

    public static boolean dispatchScroll(double xOffset, double yOffset) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object window = window(minecraft);
            Object mouse = minecraftMouse.get(minecraft);
            if (window == null || mouse == null) return false;
            mouseScroll.invoke(mouse, handle(window), xOffset, yOffset);
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return reportInputError(error);
        }
    }

    public static boolean dispatchChar(int codepoint) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object window = window(minecraft);
            Object keyboard = minecraftKeyboard.get(minecraft);
            if (window == null || keyboard == null) return false;
            keyboardChar.invoke(keyboard, handle(window), codepoint, 0);
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return reportInputError(error);
        }
    }

    public static boolean dispatchLook(float normalizedX, float normalizedY) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object window = window(minecraft);
            Object mouse = minecraftMouse.get(minecraft);
            if (window == null || mouse == null) return false;
            double x = mouseX.getDouble(mouse) + normalizedX * (Integer)windowWidth.invoke(window);
            double y = mouseY.getDouble(mouse) + normalizedY * (Integer)windowHeight.invoke(window);
            mouseMove.invoke(mouse, handle(window), x, y);
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return reportInputError(error);
        }
    }

    private static synchronized boolean reportInputError(Throwable error) {
        if (!inputDispatchErrorLogged) {
            inputDispatchErrorLogged = true;
            System.err.println("Jay input bridge dropped an event: " + error);
        }
        return false;
    }

    public static void main(String[] args) throws Exception {
        if (args.length == 0) throw new IllegalArgumentException("Minecraft main class is missing");
        System.setProperty("org.lwjgl.opengl.explicitInit", "true");

        Class<?> providerType = Class.forName("org.lwjgl.system.FunctionProvider");
        Class<?> glfw = Class.forName("org.lwjgl.glfw.GLFW");
        Method byteBufferLookup = glfw.getMethod("glfwGetProcAddress", ByteBuffer.class);
        Method textLookup = glfw.getMethod("glfwGetProcAddress", CharSequence.class);
        InvocationHandler handler = (proxy, method, values) -> {
            if (method.getDeclaringClass() == Object.class) {
                if (method.getName().equals("toString")) return "Jay Android OpenGL provider";
                if (method.getName().equals("hashCode")) return System.identityHashCode(proxy);
                if (method.getName().equals("equals")) return proxy == values[0];
            }
            if (method.getName().equals("getFunctionAddress") && values != null && values.length == 1)
                return values[0] instanceof ByteBuffer
                    ? byteBufferLookup.invoke(null, values[0])
                    : textLookup.invoke(null, values[0]);
            throw new UnsupportedOperationException(method.toString());
        };
        Object provider = Proxy.newProxyInstance(providerType.getClassLoader(), new Class<?>[] { providerType }, handler);
        Class<?> gl = Class.forName("org.lwjgl.opengl.GL");
        gl.getMethod("create", providerType).invoke(null, provider);

        String mainClass = args[0];
        String[] gameArgs = Arrays.copyOfRange(args, 1, args.length);
        Class.forName(mainClass).getMethod("main", String[].class).invoke(null, (Object)gameArgs);
    }
}
