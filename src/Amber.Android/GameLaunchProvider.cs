using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using System.Text.Json;
using System.Text.Json.Serialization;
using Uri = Android.Net.Uri;

namespace Amber.Android;

// The game runs in a second process. Give it the prepared invocation through a private,
// single-use pipe so neither account tokens nor long class paths travel in Activity intents.
[ContentProvider([Authority], Exported = false)]
public sealed class GameLaunchProvider : ContentProvider
{
    private const string Authority = "nz.work.minecraft.jay.launch";
    private static readonly object Gate = new();
    private static string? _ticket;
    private static AndroidPreparedGame? _pending;

    private const string TicketExtra = "nz.work.minecraft.jay.LAUNCH_TICKET";

    internal static void Stage(Intent intent, AndroidPreparedGame game)
    {
        lock (Gate)
        {
            _pending = game;
            _ticket = Guid.NewGuid().ToString("N");
            intent.PutExtra(TicketExtra, _ticket);
        }
    }

    internal static AndroidPreparedGame Read(Activity activity)
    {
        var ticket = activity.Intent?.GetStringExtra(TicketExtra);
        if (!Guid.TryParseExact(ticket, "N", out _)) throw new InvalidOperationException("Open this game from the launcher.");
        var uri = Uri.Parse($"content://{Authority}/{ticket}")!;
        using var input = activity.ContentResolver!.OpenInputStream(uri)
            ?? throw new IOException("Game launch expired.");
        return JsonSerializer.Deserialize(input, LaunchJson.Default.AndroidPreparedGame)
            ?? throw new InvalidDataException("Missing game invocation.");
    }

    public override ParcelFileDescriptor OpenFile(Uri uri, string mode)
    {
        AndroidPreparedGame game;
        lock (Gate)
        {
            if (mode != "r" || _pending is not { } pending || uri.LastPathSegment != _ticket)
                throw new Java.IO.FileNotFoundException("Game launch expired.");
            game = pending;
            _pending = null;
            _ticket = null;
        }
        var pipe = ParcelFileDescriptor.CreatePipe()!;
        _ = Task.Run(() =>
        {
            using var writer = new ParcelFileDescriptor.AutoCloseOutputStream(pipe[1]);
            try { writer.Write(JsonSerializer.SerializeToUtf8Bytes(game, LaunchJson.Default.AndroidPreparedGame)); }
            catch (Java.IO.IOException) { } // Game process exited before reading the invocation.
            catch (IOException) { }
            finally { writer.Close(); }
        });
        return pipe[0];
    }

    public override bool OnCreate() => true;
    public override string? GetType(Uri uri) => null;
    public override ICursor? Query(Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder) => null;
    public override Uri? Insert(Uri uri, ContentValues? values) => throw new NotSupportedException();
    public override int Update(Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => throw new NotSupportedException();
    public override int Delete(Uri uri, string? selection, string[]? selectionArgs) => throw new NotSupportedException();
}

[JsonSerializable(typeof(AndroidPreparedGame))]
internal partial class LaunchJson : JsonSerializerContext { }
