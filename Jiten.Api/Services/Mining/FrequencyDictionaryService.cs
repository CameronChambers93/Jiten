using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Frequency;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Api.Services.Mining;

public record FrequencyDictionaryDto(int Id, string Title, string? Revision, DateTime ImportedAt, int EntryCount, int RankedForms);

/// <summary>Imports Yomitan frequency dictionaries, ranking every Jiten word form once so mining can sort and filter by plain lookups.</summary>
public class FrequencyDictionaryService(JitenDbContext context, ILogger<FrequencyDictionaryService> logger)
{
    public async Task<List<FrequencyDictionaryDto>> ListAsync(CancellationToken ct) =>
        await context.FrequencyDictionaries.AsNoTracking().OrderBy(d => d.Title)
                     .Select(d => new FrequencyDictionaryDto(d.FrequencyDictionaryId, d.Title, d.Revision, d.ImportedAt, d.EntryCount, d.RankedForms))
                     .ToListAsync(ct);

    /// <summary>Reads the zip, ranks the word forms and stores them; a dictionary with the same title is replaced.</summary>
    public async Task<FrequencyDictionaryDto> ImportAsync(Stream zip, CancellationToken ct)
    {
        var file = YomitanFrequencyDictionary.Read(zip);
        var forms = await context.WordForms.AsNoTracking()
                                 .Select(f => new FrequencyForm(f.WordId, f.ReadingIndex, f.Text, f.RubyText, f.FormType))
                                 .ToListAsync(ct);
        var ranks = YomitanFrequencyDictionary.Rank(file.Entries, forms);
        var title = file.Title.Length == 0 ? "Untitled" : file.Title.Length > 200 ? file.Title[..200] : file.Title;

        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.FrequencyDictionaries.Where(d => d.Title == title).ExecuteDeleteAsync(ct);

        var dictionary = new FrequencyDictionary
        {
            Title = title, Revision = file.Revision is { Length: > 200 } r ? r[..200] : file.Revision, ImportedAt = DateTime.UtcNow,
            EntryCount = file.Entries.Count, RankedForms = ranks.Count
        };
        context.FrequencyDictionaries.Add(dictionary);
        await context.SaveChangesAsync(ct);

        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        await using (var writer = await conn.BeginBinaryImportAsync(
                         """COPY jiten."FrequencyDictionaryRanks" ("FrequencyDictionaryId", "WordId", "ReadingIndex", "Rank") FROM STDIN (FORMAT BINARY)""", ct))
        {
            foreach (var ((wordId, readingIndex), rank) in ranks)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(dictionary.FrequencyDictionaryId, ct);
                await writer.WriteAsync(wordId, ct);
                await writer.WriteAsync(readingIndex, ct);
                await writer.WriteAsync(rank, ct);
            }

            await writer.CompleteAsync(ct);
        }

        await transaction.CommitAsync(ct);
        logger.LogInformation("Imported frequency dictionary {Title}: {Entries} entries, {Forms} word forms ranked", title, file.Entries.Count, ranks.Count);
        return new FrequencyDictionaryDto(dictionary.FrequencyDictionaryId, dictionary.Title, dictionary.Revision, dictionary.ImportedAt,
                                          dictionary.EntryCount, dictionary.RankedForms);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        await context.FrequencyDictionaries.Where(d => d.FrequencyDictionaryId == id).ExecuteDeleteAsync(ct) > 0;

    /// <summary>Ranks of the given forms in one dictionary; unranked forms are absent.</summary>
    public async Task<Dictionary<(int WordId, short ReadingIndex), int>> RanksAsync(int dictionaryId, IReadOnlyCollection<int> wordIds, CancellationToken ct)
    {
        var rows = await context.FrequencyDictionaryRanks.AsNoTracking()
                                .Where(r => r.FrequencyDictionaryId == dictionaryId && wordIds.Contains(r.WordId))
                                .Select(r => new { r.WordId, r.ReadingIndex, r.Rank })
                                .ToListAsync(ct);
        return rows.ToDictionary(r => (r.WordId, r.ReadingIndex), r => r.Rank);
    }
}
