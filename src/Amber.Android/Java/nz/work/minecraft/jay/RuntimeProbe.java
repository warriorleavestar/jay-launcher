package nz.work.minecraft.jay;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

/** A tiny Jay-owned entry point used to prove that the embedded JVM can execute Java. */
public final class RuntimeProbe {
    private RuntimeProbe() {}

    public static void main(String[] args) throws Exception {
        if (args.length != 1) {
            throw new IllegalArgumentException("RuntimeProbe requires one marker path");
        }

        Path marker = Paths.get(args[0]).toAbsolutePath().normalize();
        Path parent = marker.getParent();
        if (parent == null) {
            throw new IllegalArgumentException("RuntimeProbe marker has no parent");
        }

        Files.createDirectories(parent);
        Files.write(marker, "jay-jvm-ok\n".getBytes(StandardCharsets.UTF_8));
        System.out.println("Jay Android JVM probe passed");
    }
}
