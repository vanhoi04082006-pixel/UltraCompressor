using UltraCompressor.Core.Models;

namespace UltraCompressor.Core.Storage;

public sealed class ConfigStore(string path) : IDisposable
{
    public string Path { get; } = path;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<AppConfig> LoadAsync(CancellationToken token = default)
    {
        var (value, error) = await JsonStore.TryLoadAsync<AppConfig>(Path, token);
        if (value is not null) return value;
        return new AppConfig();
    }

    public async Task SaveAsync(AppConfig config, CancellationToken token = default)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            await JsonStore.SaveAsync(Path, config, token);
        }
        catch
        {
            // Cấu hình hỏng không nên làm treo ứng dụng — lần sau dùng mặc định.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
