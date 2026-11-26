using System.Text.Json;
using System.Text.Json.Serialization;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Settings;

namespace OtimizadorPC.Infrastructure.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _lock = new();

    public JsonSettingsService() : this(DefaultPath) { }

    public JsonSettingsService(string path)
    {
        _path = path;
        Current = Load();
    }

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OtimizadorPC");

    public static string DefaultPath => Path.Combine(DataFolder, "settings.json");

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Update(Action<AppSettings> change)
    {
        lock (_lock)
        {
            change(Current);
            Save();
        }
        Changed?.Invoke(this, Current);
    }

    public void Reset()
    {
        lock (_lock)
        {
            Current = new AppSettings();
            Save();
        }
        Changed?.Invoke(this, Current);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Arquivo corrompido: volta ao padrão sem derrubar o aplicativo.
        }
        return new AppSettings();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
