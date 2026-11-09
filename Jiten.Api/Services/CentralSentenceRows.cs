using System.Security.Cryptography;
using System.Text;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Api.Services;

/// <summary>
/// The <see cref="Sentence"/> row for a text. On Postgres, jiten.normalize_sentence decides what counts as the same text;
/// elsewhere (the tests) the text is matched as it is.
/// </summary>
public static class CentralSentenceRows
{
    public static async Task<long?> FindAsync(JitenDbContext context, string text, CancellationToken ct) =>
        (await FindManyAsync(context, [text], ct)).TryGetValue(text, out var id) ? id : null;

    /// <summary>The rows of those <paramref name="texts"/> that have one, by text.</summary>
    public static async Task<Dictionary<string, long>> FindManyAsync(JitenDbContext context, IReadOnlyCollection<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        if (context.Database.GetDbConnection() is not NpgsqlConnection conn)
            return await context.Sentences.Where(s => texts.Contains(s.Text)).ToDictionaryAsync(s => s.Text, s => s.Id, ct);

        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT u.t, s.""Id"" FROM unnest(@texts) AS u(t)
            JOIN jiten.""Sentences"" s ON s.""TextHash"" = sha256(convert_to(jiten.normalize_sentence(u.t), 'UTF8'))", conn);
        cmd.Parameters.AddWithValue("texts", texts.ToArray());

        var rows = new Dictionary<string, long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows[reader.GetString(0)] = reader.GetInt64(1);
        return rows;
    }

    /// <summary>The row for a text, made if it's new. Hold the rows first (<see cref="JitenHelper.HoldSentenceRowsAsync"/>) until what points at it is saved.</summary>
    public static async Task<long> EnsureAsync(JitenDbContext context, string text, CancellationToken ct)
    {
        if (context.Database.GetDbConnection() is not NpgsqlConnection conn)
        {
            if (await FindAsync(context, text, ct) is { } id) return id;
            var row = new Sentence { TextHash = SHA256.HashData(Encoding.UTF8.GetBytes(text)), Text = text };
            context.Sentences.Add(row);
            await context.SaveChangesAsync(ct);
            return row.Id;
        }

        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        return (await JitenHelper.EnsureCentralSentences(conn, [text]))[0];
    }
}
