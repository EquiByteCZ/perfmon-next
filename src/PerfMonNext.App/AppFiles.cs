using System.IO;
using System.Text.Json;
using PerfMonNext.Core;

namespace PerfMonNext;

public static class AppFiles
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfMonNext");
    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static string CredentialsPath => Path.Combine(Root, "credentials.json");

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonDefaults.Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonDefaults.Options));
    }

    public static void TryImportCredentials(ICredentialVault vault, ISecretProtector protector)
    {
        try
        {
            if (File.Exists(CredentialsPath))
                vault.ImportProtected(File.ReadAllText(CredentialsPath), protector);
        }
        catch
        {
            // A corrupt credential file should not block the app. The user can enter passwords again.
        }
    }

    public static void SaveCredentials(ICredentialVault vault, ISecretProtector protector)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(CredentialsPath, vault.ExportProtected(protector));
    }
}

public sealed class AppSettings
{
    public string? LastWorkspacePath { get; set; }
}
