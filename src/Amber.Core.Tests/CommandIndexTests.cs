using Amber.Core.Navigation;

namespace Amber.Core.Tests;

/// <summary>Command palette search (official design §6.23).</summary>
public sealed class CommandIndexTests
{
    private static readonly CommandItem[] Items =
    [
        new("i:1", CommandKind.Instance, "生存整合包", "1.20.1 · Fabric", ["sodium", "iris"]),
        new("i:2", CommandKind.Instance, "Skyblock", "1.20.4"),
        new("a:start", CommandKind.Action, "开始游戏…", null, ["Play", "Start game"]),
        new("p:new", CommandKind.Page, "新建实例", null, ["下载", "Downloads", "New instance"]),
        new("p:tools", CommandKind.Page, "设置 › 工具", null, ["工具箱", "Toolbox"]),
        new("s:memory", CommandKind.Setting, "设置 › 游戏默认 › 内存", null, ["Memory"]),
    ];

    [Theory]
    [InlineData("sczhb", "i:1")]
    [InlineData("SCZ", "i:1")]
    [InlineData("整合", "i:1")]
    [InlineData("sky", "i:2")]
    [InlineData("下载", "p:new")]
    [InlineData("toolbox", "p:tools")]
    [InlineData("gjx", "p:tools")]
    [InlineData("memory", "s:memory")]
    [InlineData("sodium", "i:1")]
    public void FindsByTitleInitialsOtherNamesAndOldPageNames(string query, string id) =>
        Assert.Equal(id, CommandIndex.Search(query, Items).First().Item.Id);

    [Fact]
    public void PrefixesNarrowTheKind()
    {
        Assert.All(CommandIndex.Search(">", Items), m => Assert.Equal(CommandKind.Action, m.Item.Kind));
        Assert.All(CommandIndex.Search("@", Items), m => Assert.Equal(CommandKind.Instance, m.Item.Kind));
        Assert.Equal(["s:memory"], CommandIndex.Search("# 内存", Items).Select(m => m.Item.Id));
        Assert.Empty(CommandIndex.Search("@ memory", Items));
        Assert.Equal((CommandKind.Help, "modpack"), CommandIndex.ParsePrefix("?modpack"));
    }

    [Fact]
    public void NoMatchIsEmptyAndEmptyQueryListsEverything()
    {
        Assert.Empty(CommandIndex.Search("zzzzqqq", Items));
        Assert.Equal(Items.Length, CommandIndex.Search("", Items).Count);
    }

    [Fact]
    public void PinyinInitialsOfCommonCharacters()
    {
        Assert.Equal("sczhb", Pinyin.Initials("生存整合包"));
        Assert.Equal("szgj", Pinyin.Initials("设置 › 工具"));
        Assert.Equal("mc1201", Pinyin.Initials("MC 1.20.1"));
    }

    [Fact]
    public void GroupWithTheBestMatchComesFirst()
    {
        CommandItem[] items =
        [
            new("s:a", CommandKind.Setting, "Circuit breaker", "Settings"),
            new("s:b", CommandKind.Setting, "Game directory", "Settings"),
            new("p:settings", CommandKind.Page, "Settings"),
            new("v:x", CommandKind.Invite, "Join with this invite", null, ["xyz-code"]),
        ];
        var ids = CommandIndex.Search("set", items).Select(m => m.Item.Id).ToList();
        Assert.Equal("p:settings", ids[0]);
        Assert.Equal(["s:a", "s:b"], ids.Skip(1).ToList());
        Assert.Equal("v:x", CommandIndex.Search("code", items)[0].Item.Id);
    }
}
