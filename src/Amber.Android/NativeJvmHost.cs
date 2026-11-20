using System.Runtime.InteropServices;
using Android.Runtime;
using Android.Views;
using Amber.Mobile;

namespace Amber.Android;

internal static partial class NativeJvmHost
{
    private const int ExpectedAbiVersion = 16;
    public static bool GameplayControlsActive => IsAvailable && GetNativeGameplayMode() != 0;

    public static bool HasBlockTarget
    {
        get
        {
            if (!IsAvailable) return false;
            try { return GetNativeBlockTarget() != 0; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    public static int TextInputFocusState
    {
        get
        {
            if (!IsAvailable) return -1;
            try { return Math.Clamp(GetNativeTextInputFocused(), -1, 1); }
            catch (EntryPointNotFoundException) { return -1; }
        }
    }

    public static bool TextInputAt(double x, double y)
    {
        if (!IsAvailable) return false;
        try { return GetNativeTextInputAt((float)x, (float)y) != 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static bool IsAvailable
    {
        get
        {
            try { return AbiVersion() == ExpectedAbiVersion; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    public static string? LoadRuntime(string libJvmPath)
    {
        var error = LoadJvm(libJvmPath);
        return error == 0 ? null : Marshal.PtrToStringUTF8(error);
    }

    public static string? Preload(string libraryPath)
    {
        var error = PreloadLibrary(libraryPath);
        return error == 0 ? null : Marshal.PtrToStringUTF8(error);
    }

    public static string? PreloadLocal(string libraryPath)
    {
        var error = PreloadLocalLibrary(libraryPath);
        return error == 0 ? null : Marshal.PtrToStringUTF8(error);
    }

    public static void SetEnvironmentVariable(string name, string value)
    {
        var error = SetNativeEnvironmentVariable(name, value);
        if (error != 0)
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(error) ?? $"Could not set {name}.");
    }

    public static (nint Vm, nint Environment) Create(IReadOnlyList<string> options)
    {
        var strings = options.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var pointers = Marshal.AllocHGlobal(nint.Size * strings.Length);
        try
        {
            for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(pointers, i * nint.Size, strings[i]);
            var result = CreateJvm(pointers, strings.Length, out var vm, out var environment);
            if (result != 0) throw new InvalidOperationException($"JNI_CreateJavaVM failed with {result}.");
            return (vm, environment);
        }
        finally
        {
            Marshal.FreeHGlobal(pointers);
            foreach (var value in strings) Marshal.FreeCoTaskMem(value);
        }
    }

    public static void RunMain(nint environment, string className, IReadOnlyList<string> arguments)
    {
        var main = Marshal.StringToCoTaskMemUTF8(className.Replace('.', '/'));
        var strings = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var pointers = Marshal.AllocHGlobal(nint.Size * strings.Length);
        try
        {
            for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(pointers, i * nint.Size, strings[i]);
            var result = RunJavaMain(environment, main, strings.Length, pointers);
            if (result != 0)
            {
                var detail = Marshal.PtrToStringUTF8(LastError());
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? $"Java main() failed with {result}."
                    : $"Java main() failed with {result}: {detail}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointers);
            foreach (var value in strings) Marshal.FreeCoTaskMem(value);
            Marshal.FreeCoTaskMem(main);
        }
    }

    public static void SetAction(TouchControlAction action, bool pressed)
    {
        if (!IsAvailable) return;
        // Android UI callbacks run on ART's main thread. Never invoke Minecraft handlers
        // directly from here: old clients mutate Screen state synchronously and can crash the
        // render thread. The GLFW bridge consumes this snapshot from glfwPollEvents instead.
        SetNativeAction((int)action, pressed ? 1 : 0);
    }

    public static void CancelAction(TouchControlAction action)
    {
        if (!IsAvailable) return;
        CancelNativeAction((int)action);
    }

    public static void SetMovement(double x, double y)
    {
        if (!IsAvailable) return;
        SetNativeMovement((float)x, (float)y);
    }

    public static void AddLook(double x, double y)
    {
        if (!IsAvailable) return;
        AddNativeLook((float)x, (float)y);
    }

    public static void SetPointer(double x, double y, bool click)
    {
        if (!IsAvailable) return;
        SetNativePointer((float)x, (float)y, click ? 1 : 0);
    }

    public static bool DispatchJavaPointer(double x, double y, int clicks)
    {
        if (!IsAvailable) return false;
        return TryJavaPointer((float)x, (float)y, (uint)Math.Max(0, clicks));
    }

    public static int InventoryStackCount(double x, double y)
    {
        if (!IsAvailable) return 0;
        try { return GetNativeInventoryStackCount((float)x, (float)y); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    public static void SplitInventoryStack(double x, double y, int count)
    {
        if (!IsAvailable) return;
        SetNativeInventorySplit((float)x, (float)y, count);
    }

    public static void AddCodePoint(int codePoint)
    {
        if (!IsAvailable) return;
        AddNativeCodePoint((uint)codePoint);
    }

    public static void AddKey(int glfwKey)
    {
        if (!IsAvailable) return;
        AddNativeKey(glfwKey);
    }

    private static bool TryJavaKey(int key, int action)
    {
        try { return JavaKey(key, action) != 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool TryJavaMouseButton(int button, int action)
    {
        try { return JavaMouseButton(button, action) != 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool TryJavaLook(float x, float y)
    {
        try { return JavaLook(x, y) != 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool TryJavaPointer(float x, float y, uint clicks)
    {
        try { return JavaPointer(x, y, clicks) != 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static int TryJavaChar(uint codePoint)
    {
        try { return JavaChar(codePoint); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    private static int TryJavaScroll(double x, double y)
    {
        try { return JavaScroll(x, y); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    private static int TryJavaInventorySplit(float x, float y, int count)
    {
        try { return JavaInventorySplit(x, y, count); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    public static void SetSurface(Surface? surface)
    {
        if (IsAvailable) SetNativeSurface(JNIEnv.Handle, surface?.Handle ?? 0);
    }

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_abi_version")]
    private static partial int AbiVersion();

    [LibraryImport("jayjvm", EntryPoint = "jay_gameplay_mode_get")]
    private static partial int GetNativeGameplayMode();

    [LibraryImport("jayjvm", EntryPoint = "jay_block_target_get")]
    private static partial int GetNativeBlockTarget();

    [LibraryImport("jayjvm", EntryPoint = "jay_text_input_focused_get")]
    private static partial int GetNativeTextInputFocused();

    [LibraryImport("jayjvm", EntryPoint = "jay_text_input_at_get")]
    private static partial int GetNativeTextInputAt(float x, float y);

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_load", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint LoadJvm(string path);

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_preload", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint PreloadLibrary(string path);

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_preload_local", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint PreloadLocalLibrary(string path);

    [LibraryImport("jayjvm", EntryPoint = "jay_setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SetNativeEnvironmentVariable(string name, string value);

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_create")]
    private static partial int CreateJvm(nint options, int optionCount, out nint vm, out nint environment);

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_run_main")]
    private static partial int RunJavaMain(nint environment, nint className, int argumentCount, nint arguments);

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_last_error")]
    private static partial nint LastError();

    [LibraryImport("jayjvm", EntryPoint = "jay_input_set_action")]
    private static partial void SetNativeAction(int action, int pressed);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_cancel_action")]
    private static partial void CancelNativeAction(int action);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_set_movement")]
    private static partial void SetNativeMovement(float x, float y);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_add_look")]
    private static partial void AddNativeLook(float x, float y);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_set_pointer")]
    private static partial void SetNativePointer(float x, float y, int click);

    [LibraryImport("jayjvm", EntryPoint = "jay_inventory_stack_count")]
    private static partial int GetNativeInventoryStackCount(float x, float y);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_split_inventory")]
    private static partial void SetNativeInventorySplit(float x, float y, int count);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_add_codepoint")]
    private static partial void AddNativeCodePoint(uint codePoint);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_add_key")]
    private static partial void AddNativeKey(int glfwKey);

    [LibraryImport("jayjvm", EntryPoint = "jay_java_pointer")]
    private static partial int JavaPointer(float x, float y, uint clicks);

    [LibraryImport("jayjvm", EntryPoint = "jay_java_mouse_button")]
    private static partial int JavaMouseButton(int button, int action);

    [LibraryImport("jayjvm", EntryPoint = "jay_java_key")]
    private static partial int JavaKey(int key, int action);

    [LibraryImport("jayjvm", EntryPoint = "jay_java_scroll")]
    private static partial int JavaScroll(double x, double y);

    [LibraryImport("jayjvm", EntryPoint = "jay_java_char")]
    private static partial int JavaChar(uint codePoint);

    [LibraryImport("jayjvm", EntryPoint = "jay_java_look")]
    private static partial int JavaLook(float x, float y);

    [LibraryImport("jayjvm", EntryPoint = "jay_java_inventory_split")]
    private static partial int JavaInventorySplit(float x, float y, int count);

    [LibraryImport("jayjvm", EntryPoint = "jay_surface_set")]
    private static partial void SetNativeSurface(nint environment, nint surface);
}
