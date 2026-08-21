using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services;

/// <summary>
/// Registro append-only das operações sensíveis do agente, em JSONL.
/// <para>
/// Era um stub que devolvia <c>Task.CompletedTask</c> e descartava tudo em silêncio — enquanto
/// a documentação descrevia o AIB como "Zero-Trust" e o código chamava a auditoria como se ela
/// gravasse. Um log que mente é pior que log nenhum: dá confiança sem dar rastro.
/// </para>
/// <para>
/// Grava ANTES da execução, nunca depois. Um comando que trava a máquina precisa ter deixado
/// registro de que foi autorizado; auditoria pós-fato perde exatamente o caso que importa.
/// </para>
/// </summary>
public static class AuditLogService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// UTF-8 SEM BOM. O <c>Encoding.UTF8</c> padrão emite o BOM na criação do arquivo, e três
    /// bytes invisíveis no início tornam a PRIMEIRA linha do JSONL irrecuperável por qualquer
    /// parser padrão — justo a entrada mais antiga do dia.
    /// </summary>
    private static readonly UTF8Encoding _utf8SemBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Redireciona a auditoria para fora de <see cref="DirectoryService.LogsDir"/>.
    /// Existe para os testes: sem isto a suíte grava no log de auditoria REAL do usuário, e um
    /// log com entradas fabricadas por teste deixa de valer como evidência do que aconteceu.
    /// </summary>
    public static string? LogDirectoryOverride { get; set; }

    private static string LogDirectory => LogDirectoryOverride ?? DirectoryService.LogsDir;

    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Um arquivo por dia, em <see cref="DirectoryService.LogsDir"/>.</summary>
    public static string CurrentLogPath =>
        Path.Combine(LogDirectory, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

    /// <summary>
    /// Anexa uma entrada. Nunca lança: falha de auditoria não pode derrubar a conversa, mas
    /// vai para o console para não sumir calada.
    /// </summary>
    public static async Task AppendAsync(object logData)
    {
        if (logData == null) return;

        string line;
        try
        {
            line = JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                data = logData
            }, _json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIT ERRO] Falha ao serializar entrada: {ex.Message}");
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(LogDirectory);
            await File.AppendAllTextAsync(CurrentLogPath, line + Environment.NewLine, _utf8SemBom)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUDIT ERRO] Falha ao gravar em {CurrentLogPath}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}
