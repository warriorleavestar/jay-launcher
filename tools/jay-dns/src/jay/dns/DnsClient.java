package jay.dns;

import java.io.ByteArrayOutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.List;

/** A minimal DNS client (RFC 1035): one A or AAAA question over UDP, answers read with name compression. */
final class DnsClient {
    static final int TYPE_A = 1;
    static final int TYPE_AAAA = 28;
    private static final int TIMEOUT_MS = 2000;
    private static final SecureRandom RANDOM = new SecureRandom();

    private DnsClient() {
    }

    /**
     * Parses "a,b" into addresses. Only IP literals are kept, and Java parses a literal without a name lookup; this
     * runs on the first lookup, after the resolver is installed.
     */
    static List<InetAddress> servers(String list) {
        List<InetAddress> servers = new ArrayList<>();
        for (String part : list.split(",")) {
            String s = part.trim();
            if (s.isEmpty() || !(s.indexOf(':') >= 0 || s.chars().allMatch(ch -> ch == '.' || Character.isDigit(ch)))) continue;
            try {
                servers.add(InetAddress.getByName(s));
            } catch (UnknownHostException e) {
                // not a literal: skipped
            }
        }
        return servers;
    }

    /** The addresses for one question, from the first server that answers; empty when none does. */
    static List<InetAddress> query(List<InetAddress> servers, String host, int type) {
        byte[] question;
        try {
            question = question(host, type);
        } catch (IllegalArgumentException e) {
            return List.of();
        }
        for (InetAddress server : servers) {
            for (int attempt = 0; attempt < 2; attempt++) {
                try (DatagramSocket socket = new DatagramSocket()) {
                    socket.setSoTimeout(TIMEOUT_MS);
                    int id = RANDOM.nextInt(0x10000);
                    question[0] = (byte) (id >> 8);
                    question[1] = (byte) id;
                    socket.send(new DatagramPacket(question, question.length, new InetSocketAddress(server, 53)));
                    byte[] buffer = new byte[4096];
                    while (true) {
                        DatagramPacket reply = new DatagramPacket(buffer, buffer.length);
                        socket.receive(reply);
                        if (!reply.getAddress().equals(server) || reply.getLength() < 12) continue;
                        if (((buffer[0] & 0xff) << 8 | (buffer[1] & 0xff)) != id) continue;
                        List<InetAddress> answers = answers(buffer, reply.getLength(), host, type);
                        if (answers != null) return answers;
                        break;
                    }
                } catch (SocketTimeoutException e) {
                    // try again, then the next server
                } catch (Exception e) {
                    break;
                }
            }
        }
        return List.of();
    }

    static byte[] question(String host, int type) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        out.write(0);
        out.write(0); // id, filled in per send
        out.write(0x01);
        out.write(0x00); // recursion desired
        out.write(0);
        out.write(1); // one question
        for (int i = 0; i < 6; i++) out.write(0);
        String name = host.endsWith(".") ? host.substring(0, host.length() - 1) : host;
        for (String label : name.split("\\.")) {
            byte[] bytes = label.getBytes(StandardCharsets.US_ASCII);
            if (bytes.length == 0 || bytes.length > 63) throw new IllegalArgumentException(host);
            out.write(bytes.length);
            out.write(bytes, 0, bytes.length);
        }
        out.write(0);
        out.write(type >> 8);
        out.write(type);
        out.write(0);
        out.write(1); // class IN
        return out.toByteArray();
    }

    /** The answer records of the asked type, or null when the reply is truncated or an error other than "no such name". */
    static List<InetAddress> answers(byte[] m, int length, String host, int type) throws UnknownHostException {
        int flags = (m[2] & 0xff) << 8 | (m[3] & 0xff);
        if ((flags & 0x8000) == 0 || (flags & 0x0200) != 0) return null; // not a response, or truncated
        int rcode = flags & 0xf;
        if (rcode == 3) return List.of(); // no such name
        if (rcode != 0) return null;
        int questions = (m[4] & 0xff) << 8 | (m[5] & 0xff);
        int count = (m[6] & 0xff) << 8 | (m[7] & 0xff);
        int p = 12;
        for (int i = 0; i < questions; i++) p = skipName(m, p, length) + 4;
        List<InetAddress> found = new ArrayList<>();
        for (int i = 0; i < count && p < length; i++) {
            p = skipName(m, p, length);
            if (p + 10 > length) break;
            int rtype = (m[p] & 0xff) << 8 | (m[p + 1] & 0xff);
            int rdlength = (m[p + 8] & 0xff) << 8 | (m[p + 9] & 0xff);
            p += 10;
            if (p + rdlength > length) break;
            if (rtype == type && (type == TYPE_A && rdlength == 4 || type == TYPE_AAAA && rdlength == 16)) {
                byte[] addr = new byte[rdlength];
                System.arraycopy(m, p, addr, 0, rdlength);
                found.add(InetAddress.getByAddress(host, addr));
            }
            // CNAME and other records: the addresses of the alias follow in the same answer.
            p += rdlength;
        }
        return found;
    }

    private static int skipName(byte[] m, int p, int length) {
        while (p < length) {
            int len = m[p] & 0xff;
            if (len == 0) return p + 1;
            if ((len & 0xc0) == 0xc0) return p + 2;
            p += len + 1;
        }
        return length;
    }
}
