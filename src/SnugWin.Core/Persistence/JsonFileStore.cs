using System;
using System.IO;
using System.Text.Json;
using SnugWin.Core.Support;

namespace SnugWin.Core.Persistence;

public sealed class JsonFileStore<T> where T : class
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }

    public JsonFileStore(string filePath)
    {
        FilePath = filePath;
    }

    public T? LoadOrQuarantine()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 {Path.GetFileName(FilePath)} 失败，已备份损坏文件：{ex.Message}");
            try
            {
                var corruptPath = $"{FilePath}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}";
                File.Move(FilePath, corruptPath, overwrite: true);
            }
            catch
            {
                // ignore backup failure
            }
            return null;
        }
    }

    public void Save(T value)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}

public sealed class RestoreRecordStore<T> where T : class
{
    private readonly JsonFileStore<T> _store;

    public RestoreRecordStore(string filePath)
    {
        _store = new JsonFileStore<T>(filePath);
    }

    public T? Pending() => _store.LoadOrQuarantine();

    public void Record(T value) => _store.Save(value);

    public void Settle() => _store.Delete();
}
