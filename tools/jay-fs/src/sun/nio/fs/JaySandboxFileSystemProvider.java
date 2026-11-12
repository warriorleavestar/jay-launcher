package sun.nio.fs;

import java.io.IOException;
import java.nio.file.AccessDeniedException;
import java.nio.file.AccessMode;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.nio.file.attribute.DosFileAttributes;
import java.nio.file.spi.FileSystemProvider;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.List;

/**
 * The JDK's Windows file system with one change for Jay's game sandbox. The JDK answers "may I write?" with an access
 * check on the file's permissions but without its integrity label, so inside the sandbox (low integrity or an
 * AppContainer) every file looks read-only, even the ones the game is allowed to write. Zip file systems then open
 * read-only and Fabric cannot write its remapped game jar. When the JDK says no to a write check, this asks Windows
 * directly by opening the file for writing (nothing is written), or, for a folder, by creating and deleting an empty
 * file in it. Everything else is the JDK's own provider.
 */
public final class JaySandboxFileSystemProvider extends WindowsFileSystemProvider {
    public JaySandboxFileSystemProvider(FileSystemProvider platform) {
        super();
    }

    @Override
    public void checkAccess(Path path, AccessMode... modes) throws IOException {
        try {
            super.checkAccess(path, modes);
        } catch (AccessDeniedException denied) {
            boolean write = false;
            List<AccessMode> others = new ArrayList<>();
            for (AccessMode mode : modes) {
                if (mode == AccessMode.WRITE) write = true;
                else others.add(mode);
            }
            if (!write) throw denied;
            super.checkAccess(path, others.toArray(new AccessMode[0]));
            if (!canWrite(path)) throw denied;
        }
    }

    private boolean canWrite(Path path) {
        try {
            DosFileAttributes attributes = readAttributes(path, DosFileAttributes.class);
            if (attributes.isDirectory()) {
                Path probe = path.resolve(".jay-write-check-" + Long.toHexString(System.nanoTime()));
                newByteChannel(probe, EnumSet.of(StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE,
                        StandardOpenOption.DELETE_ON_CLOSE)).close();
                return true;
            }
            if (attributes.isReadOnly()) return false;
            newByteChannel(path, EnumSet.of(StandardOpenOption.WRITE)).close();
            return true;
        } catch (IOException | SecurityException | UnsupportedOperationException e) {
            return false;
        }
    }
}
