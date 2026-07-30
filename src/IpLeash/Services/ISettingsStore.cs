using IpLeash.Models;

namespace IpLeash.Services;

/// <summary>Loads and persists <see cref="AppSettings"/>. Never throws; a bad file yields defaults.</summary>
public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
