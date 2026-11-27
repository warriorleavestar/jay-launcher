using Avalonia.Platform.Storage;

namespace Amber.App.Services;

/// <summary>Owns the picked document and, for stream-only providers, a private seekable copy for the ZIP reader.</summary>
internal sealed class PackSelection : IDisposable
{
    private readonly Func<Task<Stream>>? _openRead;
    private readonly IDisposable? _owner;
    private string? _path;
    private DirectoryInfo? _temporary;
    public string Name { get; }

    public PackSelection(string path)
    {
        _path = path;
        Name = Path.GetFileName(path);
    }

    public PackSelection(IStorageFile file)
        : this(file.Name, file.TryGetLocalPath(), file.OpenReadAsync, file) { }

    internal PackSelection(string name, string? localPath, Func<Task<Stream>> openRead, IDisposable? owner = null)
    {
        Name = name;
        _path = localPath;
        _openRead = openRead;
        _owner = owner;
    }

    public async Task<string> GetPathAsync(CancellationToken ct = default)
    {
        if (_path is not null) return _path;
        using var input = await _openRead!();
        _temporary = Directory.CreateTempSubdirectory("jay-picked-pack-");
        var path = Path.Combine(_temporary.FullName, "archive.zip");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await input.CopyToAsync(output, ct);
        return _path = path;
    }

    public void Dispose()
    {
        _owner?.Dispose();
        try { _temporary?.Delete(recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
