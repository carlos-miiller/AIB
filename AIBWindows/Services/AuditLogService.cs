using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services;

/// <summary>
/// Writer append-only para o log de auditoria de aprovações do modal de
/// confirmação. Grava em <c>~/.AIB/logs/audit.log</c> no formato JSONL
/// (uma entrada JSON por linha, terminada em <c>\n</c>).
///
/// Cada chamada serializa um POCO/objeto anônimo com os campos
/// <c>ts</c>, <c>tool</c>, <c>cmd</c>, <c>level</c>, <c>cwd</c>,
/// <c>outcome</c>, <c>always_allow</c>. O caller faz fire-and-forget
/// (<c>_ = AuditLogService.AppendAsync(...)</c>) — falhas vão para
/// Console com tag <c>[AUDIT]</c> e jamais explodem o app.
///
/// Implementa D6 do fase 01. SemaphoreSlim serializa appends concorrentes
/// para garantir linhas íntegras quando o ReAct loop dispara ferramentas
/// em paralelo.
/// </summary>
public static class AuditLogService
{
    // Property (não field) para honrar mudanças de DataDirectory feitas em runtime
    // via Settings → DirectoryService.ApplyFromSettings. Um static readonly field
    // seria avaliado uma única vez no type init e gravaria silenciosamente no
    // diretório antigo após o usuário mover ~/.AIB. Resolve CR-02 da review fase 01.
    private static string FilePath => Path.Combine(DirectoryService.DataDir, "logs", "audit.log");
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    public static async Task AppendAsync(object entry)
    {
        try
        {
            string line = JsonSerializer.Serialize(entry);

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // DirectoryService.EnsureDirectories() já cria logs/ no startup e a cada
                // save de settings. Mantemos o CreateDirectory dentro do lock como
                // defesa em profundidade (idempotente) — fora do lock era race-prone.
                string? parentDir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                await File.AppendAllTextAsync(FilePath, line + "\n", Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIT] Falha ao gravar audit.log: {ex.Message}");
        }
    }
}
