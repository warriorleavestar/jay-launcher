package jay.lan;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.concurrent.Semaphore;
import java.util.concurrent.atomic.AtomicLong;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

/**
 * Jay's LAN bridge for the Windows strong sandbox. A game in an AppContainer cannot be reached from outside it over
 * loopback, so Jay cannot connect to the world the player opened to LAN. It may still connect out, and the game may
 * connect to its own ports. This agent runs inside the game and keeps one control connection out to Jay (port, this
 * launch's token and key on the command line: {@code -javaagent:jay-lan.jar=<port>:<token>:<key>}). When a room member arrives, Jay asks
 * for the game's port; the agent connects to that port on 127.0.0.1 (its own process), opens a second connection out
 * to Jay for that member, and passes bytes between the two. Nothing in the game listens for Jay and nothing is opened
 * to the network.
 * <p>
 * Lines are ASCII: the agent sends {@code JAYLAN1 <token> control <nonce>} or {@code JAYLAN1 <token> data <id>};
 * Jay answers an accepted control connection with {@code ok <proof>}, an HMAC-SHA256 under the launch key of the token
 * and the agent's fresh nonce, then sends {@code open <id> <port>}; the agent answers a failed open with
 * {@code fail <id>}. Jay accepts both kinds only from the process it started with this token, and the agent takes
 * requests only from a listener that proved it holds the key (the token is sent first, so it proves nothing).
 * <p>
 * Until a control connection is accepted, the agent tries again a few times (Jay may turn it away while it is busy).
 * Once an accepted one ends (Jay closed or restarted) the bridge stops for good and the game has to be restarted to
 * host rooms: a new Jay doesn't know this launch's key, and whatever else listens on the old port by then must not be
 * able to ask for connections to the game. At most {@link #MAX_ACTIVE}
 * players are passed on at once, each with two threads, and a connection quiet in both directions for
 * {@link #IDLE_MS} is closed (Minecraft sends keep-alives every few seconds).
 */
public final class LanBridge implements Runnable {
    private static final int MAX_LINE = 128;
    /** Players passed on at once (the slot is held from the open request until both directions end). */
    static final int MAX_ACTIVE = 64;
    /** A connection with no bytes either way for this long is closed. */
    static final int IDLE_MS = 120000;
    private final Semaphore active = new Semaphore(MAX_ACTIVE);
    private volatile boolean stopped;
    private final int jayPort;
    private final String secret;
    private final byte[] key;
    /** Waits between attempts while no control connection has been accepted yet, in milliseconds. */
    static final int[] RETRY_MS = {500, 1000, 2000, 4000, 8000, 8000, 8000, 8000};
    private final Object writeLock = new Object();
    private OutputStream control;

    private LanBridge(int jayPort, String secret, byte[] key) {
        this.jayPort = jayPort;
        this.secret = secret;
        this.key = key;
    }

    public static void premain(String args) {
        if (args == null) return;
        int colon = args.indexOf(':');
        if (colon <= 0) return;
        int port;
        try {
            port = Integer.parseInt(args.substring(0, colon));
        } catch (NumberFormatException e) {
            return;
        }
        String[] rest = args.substring(colon + 1).split(":", -1);
        if (rest.length != 2) return;
        String secret = rest[0];
        if (port <= 0 || port > 65535 || !secret.matches("[0-9a-f]{32}") || !rest[1].matches("[0-9a-f]{32}")) return;
        daemon(new LanBridge(port, secret, hex(rest[1])), "Jay LAN bridge");
    }

    private static void daemon(Runnable work, String name) {
        Thread thread = new Thread(work, name);
        thread.setDaemon(true);
        thread.start();
    }

    /** Outcome of one control connection. */
    private enum Outcome { NOT_ACCEPTED, ENDED, IMPOSTOR }

    /**
     * Serves the one accepted control connection to Jay, then stops for good (see the class comment): the game's log
     * says once that the game has to be restarted to host a room again.
     */
    @Override
    public void run() {
        try {
            for (int attempt = 0; ; attempt++) {
                Outcome outcome = Outcome.NOT_ACCEPTED;
                Socket socket = null;
                try {
                    socket = connectToJay();
                    outcome = serve(socket);
                } catch (IOException ignored) {
                    // Jay is not there, or turned this connection away before accepting it.
                } finally {
                    closeQuietly(socket);
                }
                if (outcome != Outcome.NOT_ACCEPTED || attempt >= RETRY_MS.length) break;
                Thread.sleep(RETRY_MS[attempt]);
            }
        } catch (InterruptedException ignored) {
        } catch (RuntimeException ignored) {
        } catch (Error e) {
            // Out of threads or memory: the bridge stops, the game keeps running.
        } finally {
            stop();
        }
        System.err.println("[Jay LAN bridge] Lost the connection to Jay. Restart the game to host a room from it again.");
    }

    private void stop() {
        stopped = true;
        synchronized (writeLock) {
            control = null;
        }
    }

    private Socket connectToJay() throws IOException {
        Socket socket = new Socket();
        try {
            socket.connect(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), jayPort), 5000);
            socket.setTcpNoDelay(true);
            return socket;
        } catch (IOException e) {
            closeQuietly(socket);
            throw e;
        }
    }

    /** Serves the control connection until it ends. */
    private Outcome serve(Socket socket) throws IOException {
        byte[] nonce = new byte[16];
        new SecureRandom().nextBytes(nonce);
        String challenge = toHex(nonce);
        OutputStream out = socket.getOutputStream();
        out.write(("JAYLAN1 " + secret + " control " + challenge + "\n").getBytes(StandardCharsets.US_ASCII));
        out.flush();
        InputStream in = socket.getInputStream();
        socket.setSoTimeout(15000);
        String first = readLine(in);
        socket.setSoTimeout(0);
        if (first == null) return Outcome.NOT_ACCEPTED;
        // Whatever listens there answered without knowing the key: not the Jay that started this game.
        if (!first.startsWith("ok ") || !MessageDigest.isEqual(proof(challenge).getBytes(StandardCharsets.US_ASCII),
                first.substring(3).getBytes(StandardCharsets.US_ASCII))) return Outcome.IMPOSTOR;
        synchronized (writeLock) {
            control = out;
        }
        String line;
        while ((line = readLine(in)) != null) {
            String[] parts = line.split(" ");
            if (parts.length != 3 || !parts[0].equals("open")) continue;
            final String id = parts[1];
            final int port;
            try {
                port = Integer.parseInt(parts[2]);
            } catch (NumberFormatException e) {
                continue;
            }
            if (!id.matches("[0-9]{1,19}") || port <= 0 || port > 65535) continue;
            // The slot is given back when both directions have ended (or the open failed).
            if (!active.tryAcquire()) {
                send("fail " + id + "\n");
                continue;
            }
            try {
                daemon(new Runnable() {
                    @Override
                    public void run() {
                        try {
                            open(id, port);
                        } finally {
                            active.release();
                        }
                    }
                }, "Jay LAN bridge " + id);
            } catch (Throwable e) {
                // No thread could be started (out of threads or memory): turn this one down and keep serving.
                active.release();
                send("fail " + id + "\n");
            }
        }
        return Outcome.ENDED;
    }

    /** Jay's answer to an accepted control connection: HMAC-SHA256 under the launch key, in lowercase hex. */
    String proof(String nonce) {
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(key, "HmacSHA256"));
            return toHex(mac.doFinal(("jay-lan ok " + secret + " " + nonce).getBytes(StandardCharsets.US_ASCII)));
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException(e);
        }
    }

    private static byte[] hex(String text) {
        byte[] bytes = new byte[text.length() / 2];
        for (int i = 0; i < bytes.length; i++) bytes[i] = (byte) Integer.parseInt(text.substring(2 * i, 2 * i + 2), 16);
        return bytes;
    }

    private static String toHex(byte[] bytes) {
        StringBuilder text = new StringBuilder(bytes.length * 2);
        for (byte b : bytes) text.append(Character.forDigit((b >> 4) & 15, 16)).append(Character.forDigit(b & 15, 16));
        return text.toString();
    }

    private void open(String id, int port) {
        Socket game = new Socket();
        Socket jay = null;
        try {
            game.connect(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), port), 3000);
            game.setTcpNoDelay(true);
            // Only while Jay is still there: a data connection carries the token too.
            if (stopped) throw new IOException("stopped");
            jay = connectToJay();
            OutputStream out = jay.getOutputStream();
            out.write(("JAYLAN1 " + secret + " data " + id + "\n").getBytes(StandardCharsets.US_ASCII));
            out.flush();
            game.setSoTimeout(IDLE_MS / 4);
            jay.setSoTimeout(IDLE_MS / 4);
        } catch (IOException e) {
            closeQuietly(game);
            closeQuietly(jay);
            send("fail " + id + "\n");
            return;
        }
        final Socket a = game;
        final Socket b = jay;
        final AtomicLong last = new AtomicLong(System.currentTimeMillis());
        Thread up;
        try {
            up = new Thread(new Runnable() {
                @Override
                public void run() {
                    pump(a, b, last);
                }
            }, "Jay LAN bridge " + id + " up");
            up.setDaemon(true);
            up.start();
        } catch (Throwable e) {
            closeQuietly(a);
            closeQuietly(b);
            return;
        }
        pump(b, a, last);
        // The slot is held until the other direction has ended too.
        try {
            up.join();
        } catch (InterruptedException ignored) {
        }
    }

    private void send(String text) {
        synchronized (writeLock) {
            if (control == null) return;
            try {
                control.write(text.getBytes(StandardCharsets.US_ASCII));
                control.flush();
            } catch (IOException ignored) {
                // The control connection is going away; the reader notices.
            }
        }
    }

    /**
     * Copies one direction; when either side ends, or nothing has gone either way for {@link #IDLE_MS}, both sockets
     * are closed so the other direction ends too.
     */
    private static void pump(Socket from, Socket to, AtomicLong last) {
        byte[] buffer = new byte[16 * 1024];
        try {
            InputStream in = from.getInputStream();
            OutputStream out = to.getOutputStream();
            while (true) {
                int n;
                try {
                    n = in.read(buffer);
                } catch (SocketTimeoutException quiet) {
                    if (System.currentTimeMillis() - last.get() >= IDLE_MS) return;
                    continue;
                }
                if (n < 0) return;
                last.set(System.currentTimeMillis());
                out.write(buffer, 0, n);
                out.flush();
            }
        } catch (IOException ignored) {
            // Closed by the other direction or by a peer.
        } finally {
            closeQuietly(from);
            closeQuietly(to);
        }
    }

    private static String readLine(InputStream in) throws IOException {
        StringBuilder line = new StringBuilder();
        while (true) {
            int c = in.read();
            if (c < 0) return null;
            if (c == '\n') return line.toString();
            if (line.length() >= MAX_LINE) throw new IOException("line too long");
            line.append((char) c);
        }
    }

    private static void closeQuietly(Socket socket) {
        if (socket == null) return;
        try {
            socket.close();
        } catch (IOException ignored) {
        }
    }
}
