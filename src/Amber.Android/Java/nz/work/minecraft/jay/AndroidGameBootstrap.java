package nz.work.minecraft.jay;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.lang.reflect.Field;
import java.lang.reflect.Constructor;
import java.lang.reflect.Modifier;
import java.lang.reflect.InvocationTargetException;
import java.io.IOException;
import java.io.OutputStream;
import java.io.PrintStream;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

/** Supplies LWJGL OpenGL entry points through Jay's EGL-backed GLFW bridge before Minecraft starts. */
public final class AndroidGameBootstrap {
    private static Field minecraftInstance;
    private static Method minecraftInstanceMethod;
    private static Field minecraftMouse;
    private static Field minecraftKeyboard;
    private static Field minecraftWindow;
    private static Field minecraftScreen;
    private static Field minecraftGui;
    private static Method guiScreen;
    private static Field minecraftHitResult;
    private static Field mouseX;
    private static Field mouseY;
    private static Method windowHandle;
    private static Method windowWidth;
    private static Method windowHeight;
    private static Method mouseMove;
    private static boolean mouseMoveHasDeltas;
    private static Method mouseButton;
    private static Constructor<?> mouseInput;
    private static Method mouseInputFactory;
    private static Object mouseInputFactoryOwner;
    private static boolean legacyIntegerMouseButtons;
    private static boolean oneBasedMouseButtons;
    private static Method mouseScroll;
    private static Method minecraftExecute;
    private static Method keyboardKey;
    private static Method keyboardChar;
    private static Constructor<?> keyEvent;
    private static Constructor<?> characterEvent;
    private static Method startAttack;
    private static Method startUseItem;
    private static Class<?> blockHitType;
    private static Class<?> containerScreenType;
    private static Method containerHoveredSlot;
    private static Method containerSlotClicked;
    private static Field screenWidth;
    private static Field screenHeight;
    private static Field screenFocused;
    private static Field slotId;
    private static Method slotGetItem;
    private static Field itemStackCount;
    private static Method itemStackGetCount;
    private static Object pickupClickType;
    private static boolean inputBridgeReady;
    private static boolean inputBridgeFailed;
    private static boolean inputDispatchErrorLogged;

    private AndroidGameBootstrap() {}

    /** Routes the embedded JVM's console to logcat without a compile-time Android API dependency. */
    private static final class LogcatOutputStream extends OutputStream {
        private static final int MAX_CHUNK = 3500;
        private final Method println;
        private final int priority;
        private final ByteArrayOutputStream line = new ByteArrayOutputStream();

        LogcatOutputStream(int priority) throws ReflectiveOperationException {
            this.priority = priority;
            println = Class.forName("android.util.Log").getMethod(
                "println", int.class, String.class, String.class);
        }

        @Override public synchronized void write(int value) throws IOException {
            int byteValue = value & 0xff;
            if (byteValue == '\n') flushLine();
            else if (byteValue != '\r') line.write(byteValue);
        }

        @Override public synchronized void write(byte[] bytes, int offset, int length) throws IOException {
            for (int index = 0; index < length; index++) write(bytes[offset + index]);
        }

        @Override public synchronized void flush() throws IOException {
            flushLine();
        }

        private void flushLine() throws IOException {
            if (line.size() == 0) return;
            String text = new String(line.toByteArray(), StandardCharsets.UTF_8);
            try {
                for (int start = 0; start < text.length();) {
                    int end = start;
                    int encodedLength = 0;
                    while (end < text.length()) {
                        int codePoint = text.codePointAt(end);
                        int nextLength = utf8Length(codePoint);
                        if (end > start && encodedLength + nextLength > MAX_CHUNK) break;
                        encodedLength += nextLength;
                        end += Character.charCount(codePoint);
                    }
                    println.invoke(null, priority, "JayMinecraft", text.substring(start, end));
                    start = end;
                }
                line.reset();
            } catch (ReflectiveOperationException error) {
                throw new IOException("Could not write Minecraft output to logcat", error);
            }
        }

        private static int utf8Length(int codePoint) {
            if (codePoint <= 0x7f) return 1;
            if (codePoint <= 0x7ff) return 2;
            if (codePoint <= 0xffff) return 3;
            return 4;
        }
    }

    private static void routeConsoleToLogcat() {
        try {
            System.setOut(new PrintStream(new LogcatOutputStream(4), true, "UTF-8"));
            System.setErr(new PrintStream(new LogcatOutputStream(6), true, "UTF-8"));
        } catch (ReflectiveOperationException | IOException unavailable) {
            System.err.println("Jay could not attach the embedded console to logcat: " + unavailable);
        }
    }

    private static synchronized boolean prepareInputBridge() {
        if (inputBridgeReady) return true;
        if (inputBridgeFailed) return false;
        try {
            String bridgeKind;
            try {
                prepareNamedInputBridge();
                bridgeKind = "named";
            } catch (ClassNotFoundException unavailable) {
                try {
                    prepareLegacyInputBridge();
                    bridgeKind = "legacy";
                } catch (ClassNotFoundException olderClient) {
                    prepareLegacy1201InputBridge();
                    bridgeKind = "legacy-1.20.1";
                }
            }
            Object liveMinecraft = minecraft();
            if (liveMinecraft != null) {
                minecraftExecute = accessible(liveMinecraft.getClass().getMethod(
                    "execute", Runnable.class));
            }
            inputBridgeReady = true;
            System.out.println("Jay input bridge ready: " + bridgeKind);
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            inputBridgeFailed = true;
            System.err.println("Jay input bridge is unavailable: " + error);
            return false;
        }
    }

    /** Minecraft 26.1 and newer ship unobfuscated client class and member names. */
    private static void prepareNamedInputBridge() throws ReflectiveOperationException {
        Class<?> minecraftType = Class.forName("net.minecraft.client.Minecraft");
        Class<?> mouseType = Class.forName("net.minecraft.client.MouseHandler");
        Class<?> keyboardType = Class.forName("net.minecraft.client.KeyboardHandler");
        Class<?> windowType = Class.forName("com.mojang.blaze3d.platform.Window");
        minecraftInstanceMethod = accessible(minecraftType.getDeclaredMethod("getInstance"));
        minecraftMouse = accessible(minecraftType.getDeclaredField("mouseHandler"));
        minecraftKeyboard = accessible(minecraftType.getDeclaredField("keyboardHandler"));
        minecraftWindow = accessible(minecraftType.getDeclaredField("window"));
        try {
            minecraftScreen = accessible(minecraftType.getDeclaredField("screen"));
        } catch (NoSuchFieldException movedIntoGui) {
            Class<?> guiType = Class.forName("net.minecraft.client.gui.Gui");
            minecraftGui = accessible(minecraftType.getDeclaredField("gui"));
            guiScreen = accessible(guiType.getDeclaredMethod("screen"));
        }
        minecraftHitResult = accessible(minecraftType.getDeclaredField("hitResult"));
        mouseX = accessible(mouseType.getDeclaredField("xpos"));
        mouseY = accessible(mouseType.getDeclaredField("ypos"));
        try {
            windowHandle = accessible(windowType.getDeclaredMethod("getWindow"));
        } catch (NoSuchMethodException renamedHandle) {
            windowHandle = accessible(windowType.getDeclaredMethod("handle"));
        }
        windowWidth = accessible(windowType.getDeclaredMethod("getScreenWidth"));
        windowHeight = accessible(windowType.getDeclaredMethod("getScreenHeight"));
        try {
            mouseMove = accessible(mouseType.getDeclaredMethod(
                "onMove", long.class, double.class, double.class));
        } catch (NoSuchMethodException newerRelativeMouseEvents) {
            mouseMove = accessible(mouseType.getDeclaredMethod(
                "onMove", long.class, double.class, double.class,
                double.class, double.class));
            mouseMoveHasDeltas = true;
        }
        try {
            Class<?> mouseInputType = Class.forName(
                "com.mojang.blaze3d.platform.InputConstants$Key");
            mouseButton = accessible(mouseType.getDeclaredMethod(
                "onPress", long.class, mouseInputType, int.class));
            prepareNamedMouseInputFactory(mouseInputType);
        } catch (NoSuchMethodException newerMouseEvents) {
            Class<?> mouseButtonInfo = Class.forName(
                "net.minecraft.client.input.MouseButtonInfo");
            mouseButton = accessible(mouseType.getDeclaredMethod(
                "onButton", long.class, mouseButtonInfo, int.class));
            mouseInput = accessible(mouseButtonInfo.getDeclaredConstructor(int.class, int.class));
            // The SDL-based 26.3 client follows SDL's 1/2/3 mouse-button numbering. Older
            // GLFW clients use 0/1/2, so translate only for this newer callback shape.
            oneBasedMouseButtons = true;
        }
        mouseScroll = accessible(mouseType.getDeclaredMethod(
            "onScroll", long.class, double.class, double.class));
        try {
            keyboardKey = accessible(keyboardType.getDeclaredMethod(
                "keyPress", long.class, int.class, int.class, int.class, int.class));
        } catch (NoSuchMethodException newerKeyEvents) {
            Class<?> keyEventType = Class.forName("net.minecraft.client.input.KeyEvent");
            keyEvent = accessible(keyEventType.getDeclaredConstructor(
                int.class, int.class, int.class));
            keyboardKey = accessible(keyboardType.getDeclaredMethod(
                "keyPress", long.class, int.class, keyEventType));
        }
        try {
            keyboardChar = accessible(keyboardType.getDeclaredMethod(
                "charTyped", long.class, int.class, int.class));
        } catch (NoSuchMethodException newerCharacterEvents) {
            Class<?> characterEventType = Class.forName(
                "net.minecraft.client.input.CharacterEvent");
            characterEvent = accessible(characterEventType.getDeclaredConstructor(int.class));
            keyboardChar = accessible(keyboardType.getDeclaredMethod(
                "charTyped", long.class, characterEventType));
        }
        startAttack = accessible(minecraftType.getDeclaredMethod("startAttack"));
        startUseItem = accessible(minecraftType.getDeclaredMethod("startUseItem"));
        blockHitType = Class.forName("net.minecraft.world.phys.BlockHitResult");

        try {
            Class<?> screenType = Class.forName("net.minecraft.client.gui.screens.Screen");
            Class<?> eventHandlerType = Class.forName(
                "net.minecraft.client.gui.components.events.AbstractContainerEventHandler");
            containerScreenType = Class.forName(
                "net.minecraft.client.gui.screens.inventory.AbstractContainerScreen");
            Class<?> slotType = Class.forName("net.minecraft.world.inventory.Slot");
            Class<?> itemStackType = Class.forName("net.minecraft.world.item.ItemStack");
            screenWidth = accessible(screenType.getDeclaredField("width"));
            screenHeight = accessible(screenType.getDeclaredField("height"));
            screenFocused = accessible(eventHandlerType.getDeclaredField("focused"));
            containerHoveredSlot = slotAtMethod(containerScreenType, slotType);
            slotGetItem = accessible(slotType.getDeclaredMethod("getItem"));
            itemStackGetCount = accessible(itemStackType.getDeclaredMethod("getCount"));
        } catch (ReflectiveOperationException optionalInventoryApiUnavailable) {
            // Basic movement, camera, menus and ordinary inventory clicks must keep working
            // when a future client changes only its optional exact-stack-splitting API.
            containerScreenType = null;
            System.err.println("Jay exact inventory splitting is unavailable: " +
                optionalInventoryApiUnavailable);
        }
    }

    private static void prepareNamedMouseInputFactory(Class<?> keyType)
        throws ReflectiveOperationException {
        Class<?> typeClass = Class.forName("com.mojang.blaze3d.platform.InputConstants$Type");
        Object mouseType = null;
        for (Object candidate : typeClass.getEnumConstants()) {
            if (((Enum<?>)candidate).name().equals("MOUSE")) {
                mouseType = candidate;
                break;
            }
        }
        if (mouseType == null) throw new NoSuchFieldException("InputConstants.Type.MOUSE");
        for (Method method : typeClass.getDeclaredMethods()) {
            Class<?>[] parameters = method.getParameterTypes();
            if (parameters.length == 1 && parameters[0] == int.class &&
                keyType.isAssignableFrom(method.getReturnType())) {
                mouseInputFactory = accessible(method);
                mouseInputFactoryOwner = mouseType;
                return;
            }
        }
        throw new NoSuchMethodException("InputConstants.Type mouse key factory");
    }

    private static void prepareLegacyInputBridge() throws ReflectiveOperationException {
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
            Class<?> screenType = Class.forName("gsb");
            containerScreenType = Class.forName("gti");
            Class<?> slotType = Class.forName("dji");
            Class<?> itemStackType = Class.forName("dlt");
            Class<?> clickType = Class.forName("dhu");
            screenWidth = accessible(screenType.getDeclaredField("o"));
            screenHeight = accessible(screenType.getDeclaredField("p"));
            screenFocused = focusedListenerField(screenType);
            containerHoveredSlot = accessible(containerScreenType.getDeclaredMethod(
                "b", double.class, double.class));
            containerSlotClicked = accessible(containerScreenType.getDeclaredMethod(
                "a", slotType, int.class, int.class, clickType));
            slotId = accessible(slotType.getDeclaredField("a"));
            slotGetItem = accessible(slotType.getDeclaredMethod("g"));
            itemStackCount = accessible(itemStackType.getDeclaredField("r"));
            pickupClickType = accessible(clickType.getDeclaredField("a")).get(null);
    }

    /** Official Mojang 1.20.1 client mappings (1.20.1-20230612.114412). */
    private static void prepareLegacy1201InputBridge() throws ReflectiveOperationException {
            Class<?> minecraftType = Class.forName("enn");
            Class<?> mouseType = Class.forName("eno");
            Class<?> keyboardType = Class.forName("enm");
            Class<?> windowType = Class.forName("ehn");

            minecraftInstance = accessible(minecraftType.getDeclaredField("F"));
            minecraftMouse = accessible(minecraftType.getDeclaredField("n"));
            minecraftKeyboard = accessible(minecraftType.getDeclaredField("o"));
            minecraftWindow = accessible(minecraftType.getDeclaredField("Q"));
            minecraftScreen = accessible(minecraftType.getDeclaredField("z"));
            minecraftHitResult = accessible(minecraftType.getDeclaredField("w"));
            mouseX = accessible(mouseType.getDeclaredField("e"));
            mouseY = accessible(mouseType.getDeclaredField("f"));
            windowHandle = accessible(windowType.getDeclaredMethod("i"));
            windowWidth = accessible(windowType.getDeclaredMethod("m"));
            windowHeight = accessible(windowType.getDeclaredMethod("n"));
            mouseMove = accessible(mouseType.getDeclaredMethod(
                "b", long.class, double.class, double.class));
            mouseButton = accessible(mouseType.getDeclaredMethod(
                "a", long.class, int.class, int.class, int.class));
            legacyIntegerMouseButtons = true;
            mouseScroll = accessible(mouseType.getDeclaredMethod(
                "a", long.class, double.class, double.class));
            keyboardKey = accessible(keyboardType.getDeclaredMethod(
                "a", long.class, int.class, int.class, int.class, int.class));
            keyboardChar = accessible(keyboardType.getDeclaredMethod(
                "a", long.class, int.class, int.class));
            startAttack = accessible(minecraftType.getDeclaredMethod("bj"));
            startUseItem = accessible(minecraftType.getDeclaredMethod("bs"));
            blockHitType = Class.forName("eee");
            Class<?> screenType = Class.forName("euq");
            Class<?> eventHandlerType = Class.forName("eqr");
            containerScreenType = Class.forName("evp");
            Class<?> slotType = Class.forName("ccx");
            Class<?> itemStackType = Class.forName("cfz");
            Class<?> clickType = Class.forName("cbo");
            screenWidth = accessible(screenType.getDeclaredField("g"));
            screenHeight = accessible(screenType.getDeclaredField("h"));
            screenFocused = accessible(eventHandlerType.getDeclaredField("a"));
            containerHoveredSlot = slotAtMethod(containerScreenType, slotType);
            containerSlotClicked = accessible(containerScreenType.getDeclaredMethod(
                "a", slotType, int.class, int.class, clickType));
            slotId = accessible(slotType.getDeclaredField("a"));
            slotGetItem = accessible(slotType.getDeclaredMethod("e"));
            itemStackCount = accessible(itemStackType.getDeclaredField("s"));
            pickupClickType = accessible(clickType.getDeclaredField("a")).get(null);
    }

    private static <T extends java.lang.reflect.AccessibleObject> T accessible(T value) {
        value.setAccessible(true);
        return value;
    }

    private static Method slotAtMethod(Class<?> containerType, Class<?> slotType)
        throws NoSuchMethodException {
        for (Method method : containerType.getDeclaredMethods()) {
            Class<?>[] parameters = method.getParameterTypes();
            if (!Modifier.isStatic(method.getModifiers()) && parameters.length == 2 &&
                parameters[0] == double.class && parameters[1] == double.class &&
                slotType.isAssignableFrom(method.getReturnType()))
                return accessible(method);
        }
        throw new NoSuchMethodException("container slot at coordinates");
    }

    private static Field focusedListenerField(Class<?> screenType) throws NoSuchFieldException {
        Field focused = null;
        for (Class<?> type = screenType; type != null; type = type.getSuperclass()) {
            Field interfaceField = null;
            int interfaceFields = 0;
            boolean hasBooleanState = false;
            for (Field field : type.getDeclaredFields()) {
                if (Modifier.isStatic(field.getModifiers())) continue;
                if (field.getType().isInterface()) {
                    interfaceField = field;
                    interfaceFields++;
                } else if (field.getType() == boolean.class) {
                    hasBooleanState = true;
                }
            }
            // AbstractContainerEventHandler owns exactly the focused GuiEventListener and an
            // isDragging flag. This structural match works for named and obfuscated clients and
            // avoids confusing Screen's unrelated narrator/list widgets with the focused editor.
            if (interfaceFields == 1 && hasBooleanState) focused = accessible(interfaceField);
        }
        if (focused != null) return focused;
        throw new NoSuchFieldException("focused GUI listener");
    }

    private static Object minecraft() throws ReflectiveOperationException {
        return minecraftInstanceMethod != null
            ? minecraftInstanceMethod.invoke(null)
            : minecraftInstance.get(null);
    }

    private static Object mouseInput(int button) throws ReflectiveOperationException {
        return mouseInputFactory != null
            ? mouseInputFactory.invoke(mouseInputFactoryOwner, button)
            : mouseInput.newInstance(button, 0);
    }

    private static void pressMouse(Object mouse, long window, int button, int action)
        throws ReflectiveOperationException {
        if (legacyIntegerMouseButtons)
            mouseButton.invoke(mouse, window, button, action, 0);
        else
            mouseButton.invoke(mouse, window,
                mouseInput(oneBasedMouseButtons ? button + 1 : button), action);
    }

    private static Object window(Object minecraft) throws ReflectiveOperationException {
        return minecraftWindow.get(minecraft);
    }

    private static Object screen(Object minecraft) throws ReflectiveOperationException {
        return guiScreen != null
            ? guiScreen.invoke(minecraftGui.get(minecraft))
            : minecraftScreen.get(minecraft);
    }

    private static long handle(Object window) throws ReflectiveOperationException {
        return (Long)windowHandle.invoke(window);
    }

    private static void moveMouse(Object mouse, long window, double x, double y,
                                  double deltaX, double deltaY)
        throws ReflectiveOperationException {
        if (mouseMoveHasDeltas)
            mouseMove.invoke(mouse, window, x, y, deltaX, deltaY);
        else
            mouseMove.invoke(mouse, window, x, y);
    }

    public static boolean dispatchPointer(float normalizedX, float normalizedY, int clicks) {
        if (!prepareInputBridge()) return false;
        try {
            final Object minecraft = minecraft();
            if (minecraft == null) return false;
            final float queuedX = normalizedX;
            final float queuedY = normalizedY;
            final int queuedClicks = clicks;
            // The bridge can be prepared while Minecraft is still constructing its singleton.
            // Resolve the executor again on the first real event instead of permanently falling
            // back to running UI callbacks on Android's foreign thread.
            if (minecraftExecute == null) {
                minecraftExecute = accessible(minecraft.getClass().getMethod(
                    "execute", Runnable.class));
            }
            if (minecraftExecute != null) {
                minecraftExecute.invoke(minecraft, new Runnable() {
                    @Override public void run() {
                        try {
                            dispatchPointerNow(minecraft, queuedX, queuedY, queuedClicks);
                        } catch (ReflectiveOperationException | RuntimeException error) {
                            reportInputError(error);
                        }
                    }
                });
                return true;
            }
            dispatchPointerNow(minecraft, normalizedX, normalizedY, clicks);
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return reportInputError(error);
        }
    }

    private static void dispatchPointerNow(Object minecraft, float normalizedX,
                                           float normalizedY, int clicks)
        throws ReflectiveOperationException {
            Object window = window(minecraft);
            Object mouse = minecraftMouse.get(minecraft);
            if (window == null || mouse == null) return;
            double x = Math.max(0.0, Math.min(1.0, normalizedX)) * (Integer)windowWidth.invoke(window);
            double y = Math.max(0.0, Math.min(1.0, normalizedY)) * (Integer)windowHeight.invoke(window);
            long handle = handle(window);
            // The native cursor callback is queued by Minecraft. Keep its state in sync now so a
            // press and release from the same Android gesture use the new position immediately.
            mouseX.setDouble(mouse, x);
            mouseY.setDouble(mouse, y);
            moveMouse(mouse, handle, x, y, 0.0, 0.0);
            for (int index = 0; index < clicks; index++) {
                if (screen(minecraft) == null) {
                    Object hitResult = minecraftHitResult.get(minecraft);
                    if (hitResult != null && blockHitType.isInstance(hitResult))
                        startUseItem.invoke(minecraft);
                    else
                        startAttack.invoke(minecraft);
                } else {
                    pressMouse(mouse, handle, 0, 1);
                    pressMouse(mouse, handle, 0, 0);
                }
            }
    }

    /** Returns the live count under a normalized pointer, or -1 outside a container slot. */
    public static int inventoryStackCount(float normalizedX, float normalizedY) {
        if (!prepareInputBridge()) return -1;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return -1;
            Object screen = screen(minecraft);
            if (containerScreenType == null || !containerScreenType.isInstance(screen)) return -1;
            double x = Math.max(0.0, Math.min(1.0, normalizedX)) * screenWidth.getInt(screen);
            double y = Math.max(0.0, Math.min(1.0, normalizedY)) * screenHeight.getInt(screen);
            Object slot = containerHoveredSlot.invoke(screen, x, y);
            if (slot == null) return -1;
            Object stack = slotGetItem.invoke(slot);
            if (stack == null) return 0;
            int count = itemStackGetCount != null
                ? (Integer)itemStackGetCount.invoke(stack)
                : itemStackCount.getInt(stack);
            return Math.max(0, count);
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
            Object screen = screen(minecraft);
            if (containerScreenType == null || !containerScreenType.isInstance(screen)) return false;
            double x = Math.max(0.0, Math.min(1.0, normalizedX)) * screenWidth.getInt(screen);
            double y = Math.max(0.0, Math.min(1.0, normalizedY)) * screenHeight.getInt(screen);
            Object slot = containerHoveredSlot.invoke(screen, x, y);
            if (slot == null) return false;
            Object stack = slotGetItem.invoke(slot);
            int total = stack == null ? 0 : Math.max(0, itemStackGetCount != null
                ? (Integer)itemStackGetCount.invoke(stack)
                : itemStackCount.getInt(stack));
            int selected = Math.max(1, Math.min(total, requested));
            if (total <= 0) return false;
            if (containerSlotClicked == null || pickupClickType == null) {
                // Newer clients removed the old ClickType API. Route the exact same sequence
                // through the normal screen input path: left pickup, then return one item per
                // right click until the requested quantity remains on the cursor.
                Object window = window(minecraft);
                Object mouse = minecraftMouse.get(minecraft);
                if (window == null || mouse == null) return false;
                double windowX = Math.max(0.0, Math.min(1.0, normalizedX)) *
                    (Integer)windowWidth.invoke(window);
                double windowY = Math.max(0.0, Math.min(1.0, normalizedY)) *
                    (Integer)windowHeight.invoke(window);
                long handle = handle(window);
                mouseX.setDouble(mouse, windowX);
                mouseY.setDouble(mouse, windowY);
                moveMouse(mouse, handle, windowX, windowY, 0.0, 0.0);
                pressMouse(mouse, handle, 0, 1);
                pressMouse(mouse, handle, 0, 0);
                for (int index = selected; index < total; index++) {
                    pressMouse(mouse, handle, 1, 1);
                    pressMouse(mouse, handle, 1, 0);
                }
                return true;
            }
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
                && screen(minecraft) == null
                && blockHitType.isInstance(minecraftHitResult.get(minecraft));
        } catch (ReflectiveOperationException | RuntimeException error) {
            return false;
        }
    }

    /** True while the client has captured the pointer for ordinary first-person play. */
    public static boolean isGameplayControlsActive() {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            return minecraft != null && screen(minecraft) == null;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return false;
        }
    }

    /**
     * Reports whether Minecraft's current screen has focused a text-editing widget.
     *
     * Screen and widget names are obfuscated, so this deliberately follows the focused object
     * stored by Screen's event-handler base class instead of naming EditBox. Edit widgets keep
     * their editable value in a String field; ordinary focused buttons do not.
     */
    public static boolean isTextInputFocused() {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object screen = screen(minecraft);
            return screen != null && focusedTextInput(screen) != null;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return false;
        }
    }

    /** True only when the supplied normalized screen coordinate is over the focused editor. */
    public static boolean isTextInputAt(float normalizedX, float normalizedY) {
        if (!prepareInputBridge()) return false;
        try {
            Object minecraft = minecraft();
            if (minecraft == null) return false;
            Object screen = screen(minecraft);
            if (screen == null) return false;
            Object editor = focusedTextInput(screen);
            if (editor == null) return false;
            Method hitTest = textInputHitTest(editor.getClass());
            if (hitTest == null) return false;
            double x = Math.max(0.0, Math.min(1.0, normalizedX)) * screenWidth.getInt(screen);
            double y = Math.max(0.0, Math.min(1.0, normalizedY)) * screenHeight.getInt(screen);
            return Boolean.TRUE.equals(hitTest.invoke(editor, x, y));
        } catch (ReflectiveOperationException | RuntimeException error) {
            return false;
        }
    }

    private static Object focusedTextInput(Object screen) throws ReflectiveOperationException {
        if (screenFocused == null) return null;
        Object candidate = screenFocused.get(screen);
        return candidate != null && hasEditableStringState(candidate.getClass()) ? candidate : null;
    }

    private static Method textInputHitTest(Class<?> candidateType) {
        for (String name : new String[] { "isMouseOver", "a_" }) {
            for (Class<?> type = candidateType; type != null; type = type.getSuperclass()) {
                try {
                    Method method = type.getDeclaredMethod(name, double.class, double.class);
                    if (method.getReturnType() == boolean.class) return accessible(method);
                } catch (NoSuchMethodException ignored) {
                    // Continue through the widget hierarchy and known mapped/obfuscated names.
                }
            }
        }
        return null;
    }

    private static boolean hasEditableStringState(Class<?> candidateType) {
        boolean hasString = false;
        boolean hasTextCallback = false;
        for (Class<?> type = candidateType; type != null; type = type.getSuperclass()) {
            for (Field field : type.getDeclaredFields()) {
                if (Modifier.isStatic(field.getModifiers())) continue;
                if (field.getType() == String.class) hasString = true;
                String fieldType = field.getType().getName();
                if (fieldType.equals("java.util.function.Consumer") ||
                    fieldType.equals("java.util.function.Predicate") ||
                    fieldType.equals("java.util.function.BiFunction"))
                    hasTextCallback = true;
            }
        }
        // Lists and narrator widgets may also retain labels as Strings. EditBox additionally
        // owns value-change/filter/formatter callbacks; require that editor-specific state so a
        // focused world list is never mistaken for a text field.
        return hasString && hasTextCallback;
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
            if (keyEvent != null)
                keyboardKey.invoke(keyboard, handle(window), action,
                    keyEvent.newInstance(key, key, 0));
            else
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
            if (characterEvent != null)
                keyboardChar.invoke(keyboard, handle(window),
                    characterEvent.newInstance(codepoint));
            else
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
            moveMouse(mouse, handle(window), x, y,
                normalizedX * (Integer)windowWidth.invoke(window),
                normalizedY * (Integer)windowHeight.invoke(window));
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
        routeConsoleToLogcat();
        try {
            run(args);
        } catch (Throwable failure) {
            Throwable cause = failure;
            while (cause instanceof InvocationTargetException && cause.getCause() != null)
                cause = cause.getCause();
            cause.printStackTrace(System.err);
            if (cause instanceof Exception) throw (Exception)cause;
            if (cause instanceof Error) throw (Error)cause;
            throw new RuntimeException(cause);
        }
    }

    private static void run(String[] args) throws Exception {
        if (args.length == 0) throw new IllegalArgumentException("Minecraft main class is missing");
        System.setProperty("org.lwjgl.opengl.explicitInit", "true");

        String lwjglPath = System.getProperty("org.lwjgl.libname");
        if (lwjglPath == null || lwjglPath.isEmpty())
            throw new IllegalStateException("Jay's LWJGL core library path is missing");
        // Load the JNI core from the Java application class loader before resolving any LWJGL
        // class. The native host has already opened it globally for dependency lookup; this call
        // establishes the class-loader association required by JNI native-method resolution.
        System.load(lwjglPath);

        Class<?> providerType = Class.forName("org.lwjgl.system.FunctionProvider");
        Class<?> sharedLibraryType = Class.forName("org.lwjgl.system.SharedLibrary");
        Class<?> windowApi;
        Method byteBufferLookup;
        Method textLookup;
        try {
            windowApi = Class.forName("org.lwjgl.sdl.SDLVideo");
            byteBufferLookup = windowApi.getMethod("SDL_GL_GetProcAddress", ByteBuffer.class);
            textLookup = windowApi.getMethod("SDL_GL_GetProcAddress", CharSequence.class);
        } catch (ClassNotFoundException unavailable) {
            windowApi = Class.forName("org.lwjgl.glfw.GLFW");
            byteBufferLookup = windowApi.getMethod("glfwGetProcAddress", ByteBuffer.class);
            textLookup = windowApi.getMethod("glfwGetProcAddress", CharSequence.class);
        }
        final Method byteBufferProcAddress = byteBufferLookup;
        final Method textProcAddress = textLookup;
        final String glLibraryPath = System.getProperty("jay.gl.library");
        if (glLibraryPath == null || glLibraryPath.isEmpty())
            throw new IllegalStateException("Jay's OpenGL compatibility library path is missing");
        InvocationHandler handler = (proxy, method, values) -> {
            if (method.getDeclaringClass() == Object.class) {
                if (method.getName().equals("toString")) return "Jay Android OpenGL provider";
                if (method.getName().equals("hashCode")) return System.identityHashCode(proxy);
                if (method.getName().equals("equals")) return proxy == values[0];
            }
            if (method.getName().equals("getName")) return "MobileGlues";
            if (method.getName().equals("getPath")) return glLibraryPath;
            if (method.getName().equals("address")) return 0L;
            // MobileGlues is owned by the Android host and must stay loaded for the process.
            if (method.getName().equals("free") || method.getName().equals("close")) return null;
            if (method.getName().equals("getFunctionAddress") && values != null && values.length == 1)
                return values[0] instanceof ByteBuffer
                    ? byteBufferProcAddress.invoke(null, values[0])
                    : textProcAddress.invoke(null, values[0]);
            throw new UnsupportedOperationException(method.toString());
        };
        Object provider = Proxy.newProxyInstance(
            providerType.getClassLoader(), new Class<?>[] { sharedLibraryType }, handler);
        Class<?> gl = Class.forName("org.lwjgl.opengl.GL");
        gl.getMethod("create", providerType).invoke(null, provider);

        String mainClass = args[0];
        String[] gameArgs = Arrays.copyOfRange(args, 1, args.length);
        try {
            Class.forName(mainClass).getMethod("main", String[].class).invoke(null, (Object)gameArgs);
        } catch (InvocationTargetException wrapped) {
            // Reflection otherwise hides Minecraft's real startup failure behind the generic
            // InvocationTargetException seen by the JNI host. Print and rethrow the underlying
            // exception so both logcat and Jay's launcher error identify the actionable cause.
            Throwable cause = wrapped.getCause() == null ? wrapped : wrapped.getCause();
            cause.printStackTrace(System.err);
            if (cause instanceof Exception) throw (Exception)cause;
            if (cause instanceof Error) throw (Error)cause;
            throw new RuntimeException(cause);
        }
    }
}
