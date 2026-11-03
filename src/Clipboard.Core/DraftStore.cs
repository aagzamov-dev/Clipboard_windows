using System.Text;

namespace Clipboard.Core;

public sealed record CombinedDraft(string Text, Guid[] Ids);

public sealed class DraftStore
{
    public Dictionary<Guid, string> Individual { get; } = [];
    public Dictionary<string, CombinedDraft> Combined { get; } = [];
    public bool HasChanges => Individual.Count != 0 || Combined.Count != 0;
    public IReadOnlyCollection<Guid> ProtectedIds => Individual.Keys.Concat(Combined.Values.SelectMany(d => d.Ids)).Distinct().ToArray();

    public void SetIndividual(Guid id, string original, string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > Limits.TextBytes) throw new InvalidDataException("Text draft exceeds 2 MB.");
        CheckBudget(text, Individual.Where(p => p.Key != id).Select(p => p.Value).Concat(Combined.Values.Select(d => d.Text)));
        if (text == original) Individual.Remove(id); else Individual[id] = text;
    }

    public void SetCombined(string key, string original, string text, Guid[] ids)
    {
        CheckBudget(text, Individual.Values.Concat(Combined.Where(p => p.Key != key).Select(p => p.Value.Text)));
        if (text == original) Combined.Remove(key); else Combined[key] = new(text, ids);
    }

    private static void CheckBudget(string text, IEnumerable<string> others)
    {
        if (Encoding.UTF8.GetByteCount(text) + others.Sum(t => (long)Encoding.UTF8.GetByteCount(t)) > Limits.DraftBytes)
            throw new InvalidDataException("Draft memory is full. Save or revert drafts before editing more text.");
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var values = ids.ToHashSet(); foreach (var id in values) Individual.Remove(id);
        foreach (var key in Combined.Where(p => p.Value.Ids.Any(values.Contains)).Select(p => p.Key).ToArray()) Combined.Remove(key);
    }
    public void Clear() { Individual.Clear(); Combined.Clear(); }
}
