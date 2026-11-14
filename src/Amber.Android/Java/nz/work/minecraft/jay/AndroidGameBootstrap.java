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
    private static Constructor<?> mouseInput;
    private static Method mouseScroll;
    private static Method keyboardKey;
    private static Method keyboardChar;
    private static Method startAttack;
    private static Method startUseItem;
    private static Class<?> blockHitType;
    private static boolean inputBridgeReady;
    private static boolean inputBridgeFailed;
    private static boolean inputDispatchErrorLogged;

    private AndroidGameBootstrap() {}

    private static synchronized boolean prepareInputBridge() {
        if (inputBridgeReady) return true;
        if (inputBridgeFailed) return false;
        try {
            String version = System.getProperty("jay.minecraft.version", "");
            if (!version.equals("1.21.11"))
                throw new IllegalStateException("Unsupported Minecraft input mapping: " + version);
            Class<?> minecraftType = Class.forName("gfj");
            Class<?> mouseType = Class.forName("gfk");
            Class<?> keyboardType = Class.forName("gfi");
            Class<?> windowType = Class.forName("fyk");
            Class<?> mouseInputType = Class.forName("gzd");

            minecraftInstance = accessible(minecraftType.getDeclaredField("A"));
            minecraftMouse = accessible(minecraftType.getDeclaredField("n"));
            minecraftKeyboard = accessible(minecraftType.getDeclaredField("o"));
            minecraftWindow = accessible(minecraftType.getDeclaredField("O"));
            minecraftScreen = accessible(minecraftType.getDeclaredField("x"));
            minecraftHitResult = accessible(minecraftType.getDeclaredField("u"));
            mouseX = accessible(mouseType.getDeclaredField("h"));
            mouseY = accessible(mouseType.getDeclaredField("i"));
            windowHandle = accessible(windowType.getDeclaredMethod("h"));
            windowWidth = accessible(windowType.getDeclaredMethod("m"));
            windowHeight = accessible(windowType.getDeclaredMethod("n"));
            mouseMove = accessible(mouseType.getDeclaredMethod(
                "b", long.class, double.class, double.class));
            mouseButton = accessible(mouseType.getDeclaredMethod(
                "a", long.class, mouseInputType, int.class));
            mouseInput = accessible(mouseInputType.getDeclaredConstructor(int.class, int.class));
            mouseScroll = accessible(mouseType.getDeclaredMethod(
                "c", long.class, double.class, double.class));
            keyboardKey = accessible(keyboardType.getDeclaredMethod(
                "a", long.class, int.class, int.class, int.class, int.class));
            keyboardChar = accessible(keyboardType.getDeclaredMethod(
                "a", long.class, int.class, int.class));
            startAttack = accessible(minecraftType.getDeclaredMethod("bu"));
            startUseItem = accessible(minecraftType.getDeclaredMethod("bv"));
            blockHitType = Class.forName("fti");
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
                if (minecraftScreen.get(minecraft) == null) {
                    Object hitResult = minecraftHitResult.get(minecraft);
                    if (hitResult != null && blockHitType.isInstance(hitResult))
                        startUseItem.invoke(minecraft);
                    else
                        startAttack.invoke(minecraft);
                } else {
                    mouseButton.invoke(mouse, handle, mouseInput.newInstance(0, 0), 1);
                    mouseButton.invoke(mouse, handle, mouseInput.newInstance(0, 0), 0);
                }
            }
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

    public static boolean dispatchMouseButton(int button, int action) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object window = window(minecraft);
            Object mouse = minecraftMouse.get(minecraft);
            if (window == null || mouse == null) return false;
            mouseButton.invoke(mouse, handle(window),
                mouseInput.newInstance(button, 0), action);
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
