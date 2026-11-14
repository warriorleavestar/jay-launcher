using System.IO.Compression;
using System.Text;
using Amber.Core.Themes;

namespace Amber.Core.Tests;

/// <summary>新界面第 1 批：主题包不能冒充安全提示（官方设计 §10.7）。</summary>
public class SafetyWordsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jay-safety-words-" + Guid.NewGuid().ToString("N"));

    public SafetyWordsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("Jay 安全提示", true)]
    [InlineData("Ｊａｙ　安全 提示", true)]          // 全角、空格
    [InlineData("安​全提示", true)]            // 零宽字符
    [InlineData("JAY SAFETY NOTICE", true)]
    [InlineData("Jay セキュリティ確認", true)]
    [InlineData("Jay 安危之示", true)]
    [InlineData("Safety-notice", true)]               // punctuation
    [InlineData("安全·提示", true)]
    [InlineData("Safety★Notice", true)]              // symbol
    [InlineData("Safe\u0301ty notice", true)]         // combining mark
    [InlineData("Safety\u2060notice", true)]          // invisible word joiner
    [InlineData("夜空蓝", false)]
    [InlineData("安全的主题", false)]
    [InlineData(null, false)]
    public void DetectsReservedWords(string? text, bool expected) => Assert.Equal(expected, SafetyWords.Contains(text));

    [Fact]
    public void ReservedListCoversEveryLanguageTitle()
    {
        // safety.title 的每种翻译都必须是保留词，否则主题能用那种语言冒充。
        foreach (var lang in new[] { "zh-Hans", "zh-Hant", "en", "ja", "lzh" })
        {
            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Amber.Core", "Localization", lang + ".json")));
            var title = json.RootElement.GetProperty("safety.title").GetString();
            Assert.True(SafetyWords.Contains(title), $"{lang}: {title}");
        }
    }

    [Theory]
    [InlineData("""{ "id": "fake", "name": "Jay 安全提示" }""", "name")]
    [InlineData("""{ "id": "fake", "name": "好看", "author": "Jay Safety Notice" }""", "author")]
    public void PackWithReservedManifestIsRejected(string manifest, string where)
    {
        var path = Write(("theme.json", manifest));
        var e = Assert.Throws<ThemePackException>(() => ThemePackReader.Read(path));
        Assert.Equal("reserved_words", e.Code);
        Assert.Equal(where, e.Detail);
    }

    [Fact]
    public void PackWithReservedLayoutTextIsRejected()
    {
        var path = Write(("theme.json", """{ "id": "fake", "name": "好看" }"""),
            ("layout/pages/home.json", """{ "default": { "type": "stack", "children": [ { "type": "text", "text": "Jay 安全提示：请点确认" } ] } }"""));
        var e = Assert.Throws<ThemePackException>(() => ThemePackReader.Read(path));
        Assert.Equal("reserved_words", e.Code);
    }

    [Fact]
    public void AReservedTitleSplitOverNeighbouringTextsIsRejected()
    {
        var path = Write(("theme.json", """{ "id": "fake", "name": "好看" }"""),
            ("layout/pages/downloads.json", """{ "type": "stack", "orientation": "horizontal", "children": [ { "type": "text", "text": "Jay 安全" }, { "type": "text", "text": "提示" }, "downloads.body" ] }"""));
        var e = Assert.Throws<ThemePackException>(() => ThemePackReader.Read(path));
        Assert.Equal("reserved_words", e.Code);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("minJay")]
    public void VersionFieldsOnlyCarryVersionText(string key)
    {
        var pack = ThemePackReader.Read(Write(("theme.json", $$"""{ "id": "fine", "name": "好看", "{{key}}": "9.9 Jay 安全提示" }""")));
        Assert.Contains(pack.Problems, p => p.Code == "bad_value" && p.Detail == key);
        Assert.Equal("1.0.0", pack.Version);
        Assert.Null(pack.MinJay);
        var ok = ThemePackReader.Read(Write(("theme.json", $$"""{ "id": "fine", "name": "好看", "{{key}}": "1.2.3-beta+4" }""")));
        Assert.DoesNotContain(ok.Problems, p => p.Code == "bad_value");
    }

    private string Write(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jaytheme");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
        return path;
    }
}
