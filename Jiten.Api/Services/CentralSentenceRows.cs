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
    public static async Task<long?> FindAsync(JitenDbContext context, string text, CancellationToken ct)
    {
        if (context.Database.GetDbConnection() is not NpgsqlConnection)
            return await context.Sentences.Where(s => s.Text == text).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);

        return await context.Database
                            .SqlQuery<long?>($@"SELECT ""Id"" AS ""Value"" FROM jiten.""Sentences""
                                                WHERE ""TextHash"" = sha256(convert_to(jiten.normalize_sentence({text}), 'UTF8'))")
                            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The row for a text, made if it's new.</summary>
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
