package sun.nio.fs;

import java.io.IOException;
import java.net.URI;
import java.nio.channels.SeekableByteChannel;
import java.nio.file.*;
import java.nio.file.attribute.*;
import java.nio.file.spi.FileSystemProvider;
import java.util.Map;
import java.util.Set;

/** Compile-time stand-in for the JDK's package-private class of the same name; never packaged. */
public class WindowsFileSystemProvider extends FileSystemProvider {
    public WindowsFileSystemProvider() { }
    private static UnsupportedOperationException stub() { return new UnsupportedOperationException(); }
    @Override public String getScheme() { throw stub(); }
    @Override public FileSystem newFileSystem(URI uri, Map<String, ?> env) { throw stub(); }
    @Override public FileSystem getFileSystem(URI uri) { throw stub(); }
    @Override public Path getPath(URI uri) { throw stub(); }
    @Override public SeekableByteChannel newByteChannel(Path path, Set<? extends OpenOption> options, FileAttribute<?>... attrs) throws IOException { throw stub(); }
    @Override public DirectoryStream<Path> newDirectoryStream(Path dir, DirectoryStream.Filter<? super Path> filter) throws IOException { throw stub(); }
    @Override public void createDirectory(Path dir, FileAttribute<?>... attrs) throws IOException { throw stub(); }
    @Override public void delete(Path path) throws IOException { throw stub(); }
    @Override public void copy(Path source, Path target, CopyOption... options) throws IOException { throw stub(); }
    @Override public void move(Path source, Path target, CopyOption... options) throws IOException { throw stub(); }
    @Override public boolean isSameFile(Path path, Path path2) throws IOException { throw stub(); }
    @Override public boolean isHidden(Path path) throws IOException { throw stub(); }
    @Override public FileStore getFileStore(Path path) throws IOException { throw stub(); }
    @Override public void checkAccess(Path path, AccessMode... modes) throws IOException { throw stub(); }
    @Override public <V extends FileAttributeView> V getFileAttributeView(Path path, Class<V> type, LinkOption... options) { throw stub(); }
    @Override public <A extends BasicFileAttributes> A readAttributes(Path path, Class<A> type, LinkOption... options) throws IOException { throw stub(); }
    @Override public Map<String, Object> readAttributes(Path path, String attributes, LinkOption... options) throws IOException { throw stub(); }
    @Override public void setAttribute(Path path, String attribute, Object value, LinkOption... options) throws IOException { throw stub(); }
}
