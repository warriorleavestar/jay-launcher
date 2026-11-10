using Amber.Core.Multiplayer;

namespace Amber.Core.Tests;

public class ReceivedFilesTests
{
    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("SETUP.EXE", true)]
    [InlineData("map.zip.bat", true)]
    [InlineData("run.sh", true)]
    [InlineData("mod.jar", true)]
    [InlineData("trick.exe. ", true)]
    [InlineData("map.zip", false)]
    [InlineData("exe.txt", false)]
    [InlineData("screenshot.png", false)]
    [InlineData("noextension", false)]
    public void ProgramsAndScriptsAreRecognised(string name, bool program) => Assert.Equal(program, ReceivedFiles.IsProgram(name));
}
