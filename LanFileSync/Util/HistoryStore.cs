using System.IO;
using System.Text.Json;

namespace LanFileSync;

public sealed class PeerHistoryEntry
{
    public string Host { get; set; } = "";
    public string Note { get; set; } = "";
    public string LastUsed { get; set; } = "";
    public int Connects { get; set; }

    public override string ToString()
        => string.IsNullOrEmpty(Note) ? Host : $"{Host}（{Note}）";
}

public sealed class HistoryStore
{
    private readonly string _file;

    public List<PeerHistoryEntry> Entries { get; private set; } = new();

    public HistoryStore()
    {
        _file = AppPaths.ResolveFile("history.json");
        Entries = Load();
    }

    private List<PeerHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_file))
                return new List<PeerHistoryEntry>();
            string json = File.ReadAllText(_file);
            var list = JsonSerializer.Deserialize<List<PeerHistoryEntry>>(json);
            return list ?? new List<PeerHistoryEntry>();
        }
        catch
        {
            return new List<PeerHistoryEntry>();
        }
    }

    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_file, json);
        }
        catch { }
    }

    public PeerHistoryEntry? Find(string host)
        => Entries.FirstOrDefault(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase));

    public PeerHistoryEntry? Upsert(string host, int port)
    {
        host = host.Trim();
        if (host.Length == 0)
            return null;

        var entry = Find(host);
        if (entry == null)
        {
            entry = new PeerHistoryEntry { Host = host };
            Entries.Add(entry);
        }

        entry.Connects++;
        entry.LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        Entries.Sort((a, b) => string.Compare(b.LastUsed, a.LastUsed, StringComparison.Ordinal));
        Save();
        return entry;
    }

    public void SetNote(string host, string note)
    {
        var entry = Find(host);
        if (entry == null)
            return;
        entry.Note = note.Trim();
        Save();
    }

    public bool Remove(string host)
    {
        var entry = Find(host);
        if (entry == null)
            return false;
        Entries.Remove(entry);
        Save();
        return true;
    }
}