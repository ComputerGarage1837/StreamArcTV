using System.Text.Json.Nodes;

namespace StreamArcTV.Data;

/// <summary>
/// Viewers on the VOD side. Each has their own watched list and progress, My List, sort choice and
/// new-episode memory. The first user keeps the unprefixed keys (so everything that existed before
/// users were added simply belongs to them); every other user's keys carry their id as a prefix.
/// Users live in the normal preferences file, so a backup carries them.
/// </summary>
public static class VodUsers
{
    public sealed class User
    {
        public string Id { get; init; } = "";
        public string Name { get; set; } = "";
        public string Color { get; set; } = "#7C3AED";
    }

    private const string FIRST = "u1";
    private const string KEY_USERS = "vod_users";
    private const string KEY_ACTIVE = "vod_active_user";
    private static readonly string[] Scoped = { "vod_favs_movie", "vod_favs_series", "vod_favs_live", "vod_sort", "known_episodes" };

    public static readonly string[] Colors = { "#7C3AED", "#22D3EE", "#F59E0B", "#EF4444", "#10B981", "#EC4899", "#3B82F6", "#84CC16" };

    /// True once "Who's watching?" has been settled since the app started.
    public static bool ChosenThisLaunch { get; set; }

    private static readonly object Lock = new();
    private static List<User>? _users;

    public static IReadOnlyList<User> All() { lock (Lock) return Load().ToList(); }
    public static bool HasUsers => All().Count > 0;

    private static List<User> Load()
    {
        if (_users != null) return _users;
        var list = new List<User>();
        try
        {
            if (JsonNode.Parse(Prefs.RawGet(KEY_USERS) ?? "[]") is JsonArray a)
                foreach (var n in a)
                    if (n is JsonObject o && o["id"] != null)
                        list.Add(new User { Id = o["id"]!.GetValue<string>(), Name = o["name"]?.GetValue<string>() ?? "", Color = o["color"]?.GetValue<string>() ?? Colors[0] });
        }
        catch { }
        return _users = list;
    }

    private static void Save(string? active = null)
    {
        var a = new JsonArray();
        foreach (var u in Load()) a.Add(new JsonObject { ["id"] = u.Id, ["name"] = u.Name, ["color"] = u.Color });
        Prefs.RawPut(KEY_USERS, a.ToJsonString());
        if (active != null) Prefs.RawPut(KEY_ACTIVE, active);
    }

    /// Re-reads the users (after a backup was imported).
    public static void Reload() { lock (Lock) _users = null; WatchProgress.Reload(); }

    public static User? Current
    {
        get
        {
            lock (Lock)
            {
                var list = Load();
                var id = Prefs.RawGet(KEY_ACTIVE);
                return list.FirstOrDefault(u => u.Id == id) ?? list.FirstOrDefault();
            }
        }
    }

    public static string Scope(string key)
    {
        var id = Current?.Id;
        return id == null || id == FIRST ? key : $"{id}_{key}";
    }

    public static string WatchBlob()
    {
        var id = Current?.Id;
        return id == null || id == FIRST ? "watch" : $"watch_{id}";
    }

    public static void SetActive(string id)
    {
        lock (Lock) { if (Load().All(u => u.Id != id)) return; Prefs.RawPut(KEY_ACTIVE, id); }
        WatchProgress.Reload();
    }

    public static User Add(string name, string color)
    {
        User u;
        lock (Lock)
        {
            var list = Load();
            var next = list.Select(x => int.TryParse(x.Id.TrimStart('u'), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
            u = new User { Id = "u" + next, Name = name.Trim(), Color = color };
            list.Add(u);
            Save(Prefs.RawGet(KEY_ACTIVE) == null ? u.Id : null);
        }
        return u;
    }

    public static void Update(string id, string name, string color)
    {
        lock (Lock)
        {
            var u = Load().FirstOrDefault(x => x.Id == id);
            if (u == null) return;
            u.Name = name.Trim(); u.Color = color;
            Save();
        }
    }

    public static bool NameTaken(string name, string? exceptId = null) =>
        All().Any(u => u.Id != exceptId && string.Equals(u.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// Removes the user and everything that was theirs. The last user cannot be removed.
    public static bool Remove(string id)
    {
        lock (Lock)
        {
            var list = Load();
            if (list.Count <= 1 || list.All(u => u.Id != id)) return false;
            foreach (var k in Scoped) Prefs.RawPut(id == FIRST ? k : $"{id}_{k}", null);
            Prefs.SetBlob(id == FIRST ? "watch" : $"watch_{id}", null);
            list.RemoveAll(u => u.Id == id);
            Save(Prefs.RawGet(KEY_ACTIVE) == id ? list[0].Id : null);
        }
        WatchProgress.Reload();
        return true;
    }
}
