package jay.dns;

import java.net.InetAddress;
import java.net.UnknownHostException;
import java.net.spi.InetAddressResolver;
import java.net.spi.InetAddressResolverProvider;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Stream;

/**
 * Jay's fallback name resolver for games in the Windows strong sandbox (AppContainer), Java 18 and later.
 *
 * <p>Inside an AppContainer the Windows DNS client service may refuse to answer for the game while plain UDP to the
 * same DNS server works. This provider keeps the normal lookup first and only when it fails asks the DNS servers Jay
 * passes in {@code -Djay.dns.servers=a,b} directly ({@link DnsClient}). Jay loads it with
 * {@code -Xbootclasspath/a:jay-dns.jar}; Java finds it through {@code META-INF/services}.
 */
public final class JayResolverProvider extends InetAddressResolverProvider {
    @Override
    public InetAddressResolver get(Configuration configuration) {
        return new Resolver(configuration.builtinResolver(), System.getProperty("jay.dns.servers", ""));
    }

    @Override
    public String name() {
        return "jay-dns";
    }

    static final class Resolver implements InetAddressResolver {
        private final InetAddressResolver builtin;
        private final String serverList;
        private volatile List<InetAddress> servers;

        Resolver(InetAddressResolver builtin, String serverList) {
            this.builtin = builtin;
            this.serverList = serverList;
        }

        private List<InetAddress> servers() {
            List<InetAddress> s = servers;
            if (s == null) servers = s = DnsClient.servers(serverList);
            return s;
        }

        @Override
        public Stream<InetAddress> lookupByName(String host, LookupPolicy policy) throws UnknownHostException {
            UnknownHostException failure;
            try {
                return builtin.lookupByName(host, policy);
            } catch (UnknownHostException e) {
                failure = e;
            }
            List<InetAddress> servers = servers();
            if (servers.isEmpty() || host.isEmpty() || host.equalsIgnoreCase("localhost") || host.toLowerCase().endsWith(".localhost")) throw failure;
            int c = policy.characteristics();
            boolean v4 = (c & LookupPolicy.IPV4) != 0;
            boolean v6 = (c & LookupPolicy.IPV6) != 0;
            List<InetAddress> found = new ArrayList<>();
            List<InetAddress> second = new ArrayList<>();
            boolean v6First = (c & LookupPolicy.IPV6_FIRST) != 0;
            if (v4) (v6First ? second : found).addAll(DnsClient.query(servers, host, DnsClient.TYPE_A));
            if (v6) (v6First ? found : second).addAll(DnsClient.query(servers, host, DnsClient.TYPE_AAAA));
            found.addAll(second);
            if (found.isEmpty()) throw failure;
            return found.stream();
        }

        @Override
        public String lookupByAddress(byte[] addr) throws UnknownHostException {
            return builtin.lookupByAddress(addr);
        }
    }
}
