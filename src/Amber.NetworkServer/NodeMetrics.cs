using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Amber.NetworkServer;

/// <summary>
/// The node's numbers in the Prometheus text format (version 0.0.4), served by the admin API at <c>GET /metrics</c>.
/// Only totals: no room names, addresses, fingerprints or accounts, so a scrape stored in someone's monitoring system
/// says nothing about who used the network. Pulled by the operator's own Prometheus over localhost (or an SSH tunnel)
/// with the admin token; the node never sends anything anywhere.
/// </summary>
public static class NodeMetrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Render(JayNetworkServer server)
    {
        var w = new Writer();
        var health = server.Health();
        w.Gauge("jay_node_up", "1 when EasyTier and the directory are running.", health["ok"]?.GetValue<bool>() == true ? 1 : 0);
        w.Gauge("jay_node_info", "Constant 1, labelled with what this node is.", 1,
            ("network", health["network"]?.GetValue<string>() ?? ""), ("role", health["role"]?.GetValue<string>() ?? ""),
            ("easytier", health["easytier"]?.GetValue<string>() ?? ""), ("login", health["login"]?.GetValue<bool>() == true ? "on" : "off"));
        w.Gauge("jay_node_uptime_seconds", "Seconds since the node started.", Long(health["uptime"]));

        w.Gauge("jay_directory_rooms", "Rooms in the directory, listed and hidden.", Long(health["rooms"]));
        w.Gauge("jay_directory_rooms_listed", "Rooms shown in the public room list.", Long(health["listed"]));
        w.Gauge("jay_volunteer_relays", "Volunteer relays currently handed out (absent when volunteers are off).", server.Volunteers?.Listed().Count);
        w.Gauge("jay_bans", "Entries in the ban list (absent when the node has none).", server.Bans?.List().Count);
        w.Gauge("jay_signed_records", "Signed long-lived records held (absent when the directory is off).", server.Directory?.RecordCount);

        var stats = server.Stats.ToJson();
        w.Counter("jay_directory_rooms_published_total", "New rooms accepted.", Long(stats["published"]));
        w.Counter("jay_directory_rooms_renewed_total", "Renewals of rooms already listed.", Long(stats["renewed"]));
        w.Header("jay_directory_rooms_ended_total", "Rooms that left the directory, by why.", "counter");
        w.Sample("jay_directory_rooms_ended_total", Long(stats["withdrawn"]), ("reason", "withdrawn"));
        w.Sample("jay_directory_rooms_ended_total", Long(stats["expired"]), ("reason", "expired"));
        w.Sample("jay_directory_rooms_ended_total", Long(stats["removed"]), ("reason", "removed"));
        w.Header("jay_directory_rooms_rejected_total", "Rooms the directory turned down, by rule.", "counter");
        foreach (var (code, n) in stats["rejected"]?.AsObject() ?? [])
            w.Sample("jay_directory_rooms_rejected_total", Long(n), ("reason", code));
        w.Header("jay_logins_total", "Network logins, by result.", "counter");
        w.Sample("jay_logins_total", Long(stats["loginOk"]), ("result", "ok"));
        w.Sample("jay_logins_total", Long(stats["loginFail"]), ("result", "fail"));
        return w.ToString();
    }

    // Health and stats hold ints and longs; read either.
    private static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : node is JsonValue i && i.TryGetValue<int>(out var n) ? n : 0;

    private sealed class Writer
    {
        private readonly StringBuilder _out = new();

        public void Header(string name, string help, string type)
        {
            _out.Append("# HELP ").Append(name).Append(' ').Append(help.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
            _out.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        }

        public void Gauge(string name, string help, long? value, params (string Name, string Value)[] labels)
        {
            if (value is not { } v) return;
            Header(name, help, "gauge");
            Sample(name, v, labels);
        }

        public void Counter(string name, string help, long value)
        {
            Header(name, help, "counter");
            Sample(name, value);
        }

        public void Sample(string name, long value, params (string Name, string Value)[] labels)
        {
            _out.Append(name);
            if (labels.Length > 0)
            {
                _out.Append('{');
                for (var i = 0; i < labels.Length; i++)
                {
                    if (i > 0) _out.Append(',');
                    _out.Append(labels[i].Name).Append("=\"").Append(Escape(labels[i].Value)).Append('"');
                }
                _out.Append('}');
            }
            _out.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        public override string ToString() => _out.ToString();
    }
}
