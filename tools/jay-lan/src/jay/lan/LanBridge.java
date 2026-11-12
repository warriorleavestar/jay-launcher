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
 * secret on the command line: {@code -javaagent:jay-lan.jar=<port>:<secret>}). When a room member arrives, Jay asks
 * for the game's port; the agent connects to that port on 127.0.0.1 (its own process), opens a second connection out
 * to Jay for that member, and passes bytes between the two. Nothing in the game listens for Jay and nothing is opened
 * to the network.
 * <p>
 * Lines are ASCII: the agent sends {@code JAYLAN1 <secret> control} or {@code JAYLAN1 <secret> data <id>};
 * Jay sends {@code open <id> <port>}; the agent answers a failed open with {@code fail <id>}.
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
        if (port <= 0 || port > 65535 || !secret.matches("[0-9a-f]{32,64}")) return;
        daemon(new LanBridge(port, secret), "Jay LAN bridge");
    }

    private static void daemon(Runnable work, String name) {
        Thread thread = new Thread(work, name);
        thread.setDaemon(true);
        thread.start();
    }

    /** Keeps the control connection up while the game runs (Jay may not be listening yet, or may come back). */
    @Override
    public void run() {
        long wait = 1000;
        while (true) {
            try {
                Socket socket = connectToJay();
                try {
                    wait = 1000;
                    serve(socket);
                } finally {
                    synchronized (writeLock) {
                        control = null;
                    }
                    closeQuietly(socket);
                }
            } catch (IOException ignored) {
                // Jay is not there (yet): try again later.
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

    private void serve(Socket socket) throws IOException {
        OutputStream out = socket.getOutputStream();
        out.write(("JAYLAN1 " + secret + " control\n").getBytes(StandardCharsets.US_ASCII));
        out.flush();
        synchronized (writeLock) {
            control = out;
        }
        InputStream in = socket.getInputStream();
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
            if (!id.matches("[0-9]{1,18}") || port <= 0 || port > 65535) continue;
            daemon(new Runnable() {
                @Override
                public void run() {
                    open(id, port);
                }
            }, "Jay LAN bridge " + id);
        }
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
