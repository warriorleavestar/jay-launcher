package jay.lan;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;

/**
 * Jay's LAN bridge for the Windows strong sandbox. A game in an AppContainer cannot be reached from outside it over
 * loopback, so Jay cannot connect to the world the player opened to LAN. It may still connect out, and the game may
 * connect to its own ports. This agent runs inside the game and keeps one control connection out to Jay (port and
 * this launch's token on the command line: {@code -javaagent:jay-lan.jar=<port>:<token>}). When a room member arrives, Jay asks
 * for the game's port; the agent connects to that port on 127.0.0.1 (its own process), opens a second connection out
 * to Jay for that member, and passes bytes between the two. Nothing in the game listens for Jay and nothing is opened
 * to the network.
 * <p>
 * Lines are ASCII: the agent sends {@code JAYLAN1 <token> control} or {@code JAYLAN1 <token> data <id>};
 * Jay answers an accepted control connection with {@code ok}, then sends {@code open <id> <port>}; the agent answers a
 * failed open with {@code fail <id>}. Jay accepts both kinds only from the process it started with this token.
 */
public final class LanBridge implements Runnable {
    private static final int MAX_LINE = 128;
    private final int jayPort;
    private final String secret;
    private final Object writeLock = new Object();
    private OutputStream control;

    private LanBridge(int jayPort, String secret) {
        this.jayPort = jayPort;
        this.secret = secret;
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
        String secret = args.substring(colon + 1);
        if (port <= 0 || port > 65535 || !secret.matches("[0-9a-f]{32}")) return;
        daemon(new LanBridge(port, secret), "Jay LAN bridge");
    }

    private static void daemon(Runnable work, String name) {
        Thread thread = new Thread(work, name);
        thread.setDaemon(true);
        thread.start();
    }

    /**
     * Keeps the control connection up while the game runs: a dropped connection is retried with a growing pause. The
     * port and token belong to the Jay that started the game, so once that Jay is gone (closed or restarted, which picks
     * a new port and token) nothing answers again and the game has to be restarted to host rooms; that is said once in
     * the game's log.
     */
    @Override
    public void run() {
        long wait = 1000;
        int failures = 0;
        boolean told = false;
        while (true) {
            try {
                Socket socket = connectToJay();
                try {
                    // The pause only starts over once Jay accepted this connection, not merely because TCP connected.
                    if (serve(socket)) {
                        wait = 1000;
                        failures = 0;
                    }
                } finally {
                    synchronized (writeLock) {
                        control = null;
                    }
                    closeQuietly(socket);
                }
            } catch (IOException ignored) {
                // Jay is not there: try again later.
            }
            if (++failures >= 5 && !told) {
                told = true;
                System.err.println("[Jay LAN bridge] Lost the connection to Jay. Restart the game to host a room from it again.");
            }
            try {
                Thread.sleep(wait);
            } catch (InterruptedException e) {
                return;
            }
            wait = Math.min(wait * 2, 30000);
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

    /** Serves one control connection; true when Jay accepted it (said "ok"). */
    private boolean serve(Socket socket) throws IOException {
        OutputStream out = socket.getOutputStream();
        out.write(("JAYLAN1 " + secret + " control\n").getBytes(StandardCharsets.US_ASCII));
        out.flush();
        synchronized (writeLock) {
            control = out;
        }
        InputStream in = socket.getInputStream();
        String first = readLine(in);
        if (!"ok".equals(first)) return false;
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
            daemon(new Runnable() {
                @Override
                public void run() {
                    open(id, port);
                }
            }, "Jay LAN bridge " + id);
        }
        return true;
    }

    private void open(String id, int port) {
        Socket game = new Socket();
        Socket jay = null;
        try {
            game.connect(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), port), 3000);
            game.setTcpNoDelay(true);
            jay = connectToJay();
            OutputStream out = jay.getOutputStream();
            out.write(("JAYLAN1 " + secret + " data " + id + "\n").getBytes(StandardCharsets.US_ASCII));
            out.flush();
        } catch (IOException e) {
            closeQuietly(game);
            closeQuietly(jay);
            send("fail " + id + "\n");
            return;
        }
        final Socket a = game;
        final Socket b = jay;
        daemon(new Runnable() {
            @Override
            public void run() {
                pump(a, b);
            }
        }, "Jay LAN bridge " + id + " up");
        pump(b, a);
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

    /** Copies one direction; when either side ends, both sockets are closed so the other direction ends too. */
    private static void pump(Socket from, Socket to) {
        byte[] buffer = new byte[16 * 1024];
        try {
            InputStream in = from.getInputStream();
            OutputStream out = to.getOutputStream();
            int n;
            while ((n = in.read(buffer)) >= 0) {
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
