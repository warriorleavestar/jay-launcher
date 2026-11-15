using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amber.Core.Java;
using Amber.Core.Net;

namespace Amber.Core.Tests;

/// <summary>Mojang 不提供 Java 时的退路：ARM Mac/Windows 退回 x64 版，ARM Linux 下 Temurin（含安全解压）。</summary>
public class JavaFallbackTests
{
    [Fact]
    public void ArmMacAndWindows_FallBackToX64()
    {
        Assert.Equal(["mac-os-arm64", "mac-os"], JavaRuntimeInstaller.PlatformCandidates("mac-os-arm64"));
        Assert.Equal(["windows-arm64", "windows-x64"], JavaRuntimeInstaller.PlatformCandidates("windows-arm64"));
        Assert.Equal(["linux"], JavaRuntimeInstaller.PlatformCandidates("linux"));
        Assert.Equal(["linux-arm64"], JavaRuntimeInstaller.PlatformCandidates("linux-arm64"));
    }

    [Fact]
    public void Temurin_OnlyForPlatformsMojangLacks()
    {
        Assert.True(TemurinInstaller.Supports("linux-arm64"));
        Assert.False(TemurinInstaller.Supports("linux"));
        Assert.False(TemurinInstaller.Supports("mac-os-arm64"));
        Assert.Equal(8, TemurinInstaller.FeatureFor(8));
        Assert.Equal(17, TemurinInstaller.FeatureFor(16));
        Assert.Equal(21, TemurinInstaller.FeatureFor(21));
        Assert.Null(TemurinInstaller.FeatureFor(99));
        Assert.Equal("https://api.adoptium.net/v3/assets/latest/17/hotspot?architecture=aarch64&image_type=jre&os=linux&vendor=eclipse",
            TemurinInstaller.AssetsUrl(17, "linux", "aarch64"));
    }

    [Fact]
    public void PickPackage_NeedsHttpsTarAndSha256()
    {
        var sha = new string('a', 64);
        var json = """
        [
          {"binary":{"package":{"link":"http://x/jre.tar.gz","checksum":"SHA","size":1}}},
          {"binary":{"package":{"link":"https://x/jre.zip","checksum":"SHA","size":1}}},
          {"binary":{"package":{"link":"https://x/jre.tar.gz","checksum":"short","size":1}}},
          {"binary":{"package":{"link":"https://x/ok.tar.gz","checksum":"SHA","size":42}}}
        ]
        """.Replace("SHA", sha);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(new TemurinInstaller.Package("https://x/ok.tar.gz", sha, 42), TemurinInstaller.PickPackage(doc.RootElement));
        using var empty = JsonDocument.Parse("{}");
        Assert.Null(TemurinInstaller.PickPackage(empty.RootElement));
    }

    [Fact]
    public void Extract_StripsTopFolderAndKeepsLinks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jay-temurin-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = Archive(tar =>
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "jdk-17-jre/"));
                tar.WriteEntry(File("jdk-17-jre/bin/java", "#!/bin/sh\n", 0x1ED));
                tar.WriteEntry(File("jdk-17-jre/lib/libjvm.so", "elf", 0x1A4));
                if (!OperatingSystem.IsWindows()) tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "jdk-17-jre/lib/current") { LinkName = "libjvm.so" });
            });
            TemurinInstaller.Extract(archive, dir);
            Assert.Equal("#!/bin/sh\n", System.IO.File.ReadAllText(Path.Combine(dir, "bin", "java")));
            Assert.False(Directory.Exists(Path.Combine(dir, "jdk-17-jre")));
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(System.IO.File.GetUnixFileMode(Path.Combine(dir, "bin", "java")).HasFlag(UnixFileMode.UserExecute));
                Assert.Equal("elf", System.IO.File.ReadAllText(Path.Combine(dir, "lib", "current")));
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("abslink")]
    [InlineData("uplink")]
    public void Extract_RejectsEscapes(string kind)
    {
        if (kind != "file" && OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "jay-temurin-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = Archive(tar => tar.WriteEntry(kind switch
            {
                "file" => File("jdk/../../evil", "x", 0x1A4),
                "abslink" => new PaxTarEntry(TarEntryType.SymbolicLink, "jdk/lib/a") { LinkName = "/etc/passwd" },
                _ => new PaxTarEntry(TarEntryType.SymbolicLink, "jdk/lib/a") { LinkName = "../../../evil" },
            }));
            Assert.Throws<InvalidDataException>(() => TemurinInstaller.Extract(archive, dir));
            Assert.False(System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "evil")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>真的从 Adoptium 下一个 ARM Linux 的 Java 8 JRE（只验证下载、校验、解压，不运行）。设 AMBER_NET_TEST=1 才跑。</summary>
    [EnvFact("AMBER_NET_TEST", "1")]
    public async Task Temurin_RealDownload()
    {
        if (Environment.GetEnvironmentVariable("AMBER_NET_TEST") != "1" || OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "jay-temurin-net-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient();
            var installer = new TemurinInstaller(new Downloader(http, SourceChain.OfficialFirst), root) { PlatformOverride = "linux-arm64" };
            var exe = await installer.InstallAsync(8, null, CancellationToken.None);
            Assert.Equal(Path.Combine(root, "runtime", "temurin-8", "linux-arm64", "temurin-8", "bin", "java"), exe);
            Assert.True(System.IO.File.Exists(Path.Combine(root, "runtime", "temurin-8", "linux-arm64", "temurin-8", "release")));
            Assert.False(System.IO.File.Exists(Path.Combine(root, "runtime", "temurin-8", "linux-arm64", "download.tar.gz")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static PaxTarEntry File(string name, string content, int mode) =>
        new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)), Mode = (UnixFileMode)mode };

    private static MemoryStream Archive(Action<TarWriter> write)
    {
        var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
            write(tar);
        output.Position = 0;
        return output;
    }
}
