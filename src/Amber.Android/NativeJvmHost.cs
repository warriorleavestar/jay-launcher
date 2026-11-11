using System.Runtime.InteropServices;
using Android.Runtime;
using Android.Views;
using Amber.Mobile;

namespace Amber.Android;

internal static partial class NativeJvmHost
{
    private const int ExpectedAbiVersion = 2;

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
        var main = Marshal.StringToCoTaskMemUTF8(className);
        var strings = arguments.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var pointers = Marshal.AllocHGlobal(nint.Size * strings.Length);
        try
        {
            for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(pointers, i * nint.Size, strings[i]);
            var result = RunJavaMain(environment, main, strings.Length, pointers);
            if (result != 0) throw new InvalidOperationException($"Java main() failed with {result}.");
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
        if (IsAvailable) SetNativeAction((int)action, pressed ? 1 : 0);
    }

    public static void SetMovement(double x, double y)
    {
        if (IsAvailable) SetNativeMovement((float)x, (float)y);
    }

    public static void AddLook(double x, double y)
    {
        if (IsAvailable) AddNativeLook((float)x, (float)y);
    }

    public static void SetSurface(Surface? surface)
    {
        if (IsAvailable) SetNativeSurface(JNIEnv.Handle, surface?.Handle ?? 0);
    }

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_abi_version")]
    private static partial int AbiVersion();

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_load", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint LoadJvm(string path);

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_create")]
    private static partial int CreateJvm(nint options, int optionCount, out nint vm, out nint environment);

    [LibraryImport("jayjvm", EntryPoint = "jay_jvm_run_main")]
    private static partial int RunJavaMain(nint environment, nint className, int argumentCount, nint arguments);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_set_action")]
    private static partial void SetNativeAction(int action, int pressed);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_set_movement")]
    private static partial void SetNativeMovement(float x, float y);

    [LibraryImport("jayjvm", EntryPoint = "jay_input_add_look")]
    private static partial void AddNativeLook(float x, float y);

    [LibraryImport("jayjvm", EntryPoint = "jay_surface_set")]
    private static partial void SetNativeSurface(nint environment, nint surface);
}
