namespace Jiten.Core.Data;

/// <summary>A Yomitan frequency dictionary an admin imported, so mining can sort by the same ranks Yomitan puts on cards.</summary>
public class FrequencyDictionary
{
    public int FrequencyDictionaryId { get; set; }
    public required string Title { get; set; }
    public string? Revision { get; set; }
    public DateTime ImportedAt { get; set; }

    /// <summary>Frequency entries in the dictionary's term meta banks.</summary>
    public int EntryCount { get; set; }

    /// <summary>Jiten word forms the dictionary gave a rank.</summary>
    public int RankedForms { get; set; }
}

/// <summary>A word form's rank in a frequency dictionary (1 = most frequent).</summary>
public class FrequencyDictionaryRank
{
    public int FrequencyDictionaryId { get; set; }
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
    public int Rank { get; set; }
}
