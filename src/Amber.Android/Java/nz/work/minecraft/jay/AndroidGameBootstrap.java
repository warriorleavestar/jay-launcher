package nz.work.minecraft.jay;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.nio.ByteBuffer;
import java.util.Arrays;

/** Supplies LWJGL OpenGL entry points through Jay's EGL-backed GLFW bridge before Minecraft starts. */
public final class AndroidGameBootstrap {
    private AndroidGameBootstrap() {}

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
