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
    [InlineData("launcher.desktop", true)]
    [InlineData("app.msix", true)]
    [InlineData("setup.appinstaller", true)]
    [InlineData("link.URL", true)]
    [InlineData("help.chm", true)]
    [InlineData("applet.jnlp", true)]
    [InlineData("desktop.txt", false)]
    [InlineData("exe.txt", false)]
    [InlineData("screenshot.png", false)]
    [InlineData("noextension", false)]
    public void ProgramsAndScriptsAreRecognised(string name, bool program) => Assert.Equal(program, ReceivedFiles.IsProgram(name));
}
