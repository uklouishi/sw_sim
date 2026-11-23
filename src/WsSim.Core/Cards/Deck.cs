namespace WsSim.Core.Cards;

/// <summary>卡组：50 张卡的定义列表（顺序无关，开局会洗切）。</summary>
public sealed class Deck
{
    public string Name { get; }
    public IReadOnlyList<CardDefinition> Cards { get; }

    public Deck(string name, IReadOnlyList<CardDefinition> cards)
    {
        Name = name;
        Cards = cards;
    }

    /// <summary>
    /// 读取原模拟器格式的卡组文件：每行一个卡号。空行和以 # 或 // 开头的行忽略。
    /// </summary>
    public static Deck LoadFile(string path, CardDatabase db) =>
        Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllLines(path), db);

    public static Deck Parse(string name, IEnumerable<string> lines, CardDatabase db)
    {
        var cards = new List<CardDefinition>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            cards.Add(db.Get(line));
        }
        return new Deck(name, cards);
    }

    public const int DeckSize = 50;
    public const int MaxCopiesPerName = 4;
    public const int MaxClimax = 8;

    /// <summary>基本构筑检查：50 张、同名最多 4 张、名场面最多 8 张。赛制和禁限卡留到卡组编辑阶段。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Cards.Count != DeckSize) errors.Add($"卡组应为 {DeckSize} 张，实际 {Cards.Count} 张");
        foreach (var g in Cards.GroupBy(c => c.Name).Where(g => g.Count() > MaxCopiesPerName))
            errors.Add($"同名卡「{g.Key}」有 {g.Count()} 张，最多 {MaxCopiesPerName} 张");
        int cx = Cards.Count(c => c.IsClimax);
        if (cx > MaxClimax) errors.Add($"名场面有 {cx} 张，最多 {MaxClimax} 张");
        return errors;
    }
}
