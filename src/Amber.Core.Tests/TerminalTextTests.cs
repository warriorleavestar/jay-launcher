using Amber.Core.Diagnostics;

namespace Amber.Core.Tests;

/// <summary>Server-supplied text printed by the developer CLI can't drive the terminal (UI security review N, F30).</summary>
public sealed class TerminalTextTests
{
    [Theory]
    [InlineData("A Minecraft Server", "A Minecraft Server")]
    [InlineData("中文 MOTD §6金色", "中文 MOTD §6金色")]
    [InlineData("\u001b]52;c;ZXZpbA==\u0007", "\\u001b]52;c;ZXZpbA==\\u0007")]
    [InlineData("ok\u001b[2J\u001b[H", "ok\\u001b[2J\\u001b[H")]
    [InlineData("motd\nSMOKE PASS", "motd\\u000aSMOKE PASS")]
    [InlineData("host\r", "host\\u000d")]
    [InlineData("abc‮dcba", "abc\\u202edcba")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ControlAndDirectionCharactersBecomeVisible(string? input, string expected) => Assert.Equal(expected, TerminalText.Clean(input));
}
