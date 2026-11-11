using System.Text;
using System.Text.Json;

namespace PerfMonNext.Core;

public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedBytes);
}

public interface ICredentialVault
{
    void Set(string machineId, string userName, string password);
    bool TryGet(string machineId, out string userName, out string password);
    void Remove(string machineId);
    string ExportProtected(ISecretProtector protector);
    void ImportProtected(string json, ISecretProtector protector);
}

public sealed class MemoryCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, (string User, string Password)> _items = new(StringComparer.Ordinal);

    public void Set(string machineId, string userName, string password) =>
        _items[machineId] = (userName, password);

    public bool TryGet(string machineId, out string userName, out string password)
    {
        if (_items.TryGetValue(machineId, out var item))
        {
            userName = item.User;
            password = item.Password;
            return true;
        }

        userName = "";
        password = "";
        return false;
    }

    public void Remove(string machineId) => _items.Remove(machineId);

    public string ExportProtected(ISecretProtector protector)
    {
        var entries = _items.Select(pair => new ProtectedEntry
        {
            MachineId = pair.Key,
            UserName = pair.Value.User,
            Secret = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(pair.Value.Password)))
        }).ToList();
        return JsonSerializer.Serialize(new ProtectedFile { Version = 1, Entries = entries }, JsonDefaults.Options);
    }

    public void ImportProtected(string json, ISecretProtector protector)
    {
        var file = JsonSerializer.Deserialize<ProtectedFile>(json, JsonDefaults.Options)
            ?? throw new WorkspaceFormatException("Credential file is empty.");
        if (file.Version > 1)
            throw new WorkspaceFormatException("Credential file is newer than this build.");
        _items.Clear();
        foreach (var entry in file.Entries)
        {
            var password = Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(entry.Secret)));
            _items[entry.MachineId] = (entry.UserName, password);
        }
    }

    private sealed class ProtectedFile
    {
        public int Version { get; set; }
        public List<ProtectedEntry> Entries { get; set; } = [];
    }

    private sealed class ProtectedEntry
    {
        public string MachineId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Secret { get; set; } = "";
    }
}
