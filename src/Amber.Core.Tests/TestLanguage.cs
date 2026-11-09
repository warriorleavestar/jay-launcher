using System.Runtime.CompilerServices;
using Amber.Core.Localization;

namespace Amber.Core.Tests;

/// <summary>
/// Many existing tests check the Simplified Chinese wording of messages. The app's source and fallback
/// language is English, so start every test run in zh-Hans explicitly instead of relying on the default.
/// </summary>
internal static class TestLanguage
{
    [ModuleInitializer]
    internal static void Init() => L.SetLanguage("zh-Hans");
}
