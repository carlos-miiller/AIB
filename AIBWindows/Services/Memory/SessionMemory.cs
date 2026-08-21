using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

namespace AIB.Services.Memory;

/// <summary>
/// Persistência da memória de uma sessão em <c>~/.AIB/memory/sessions/{id}/</c>.
/// <para>
/// <c>raw.jsonl</c> NUNCA é apagado. Resumo é perda irreversível, e resumo ruim aqui não gera
/// inconsistência de enredo — gera agente agindo sobre informação errada com run_command na
/// mão. O cru fora do contexto é a rede de segurança: sai do prompt, não sai do disco.
/// </para>
/// </summary>
public sealed class SessionMemory
{
    /// <summary>
    /// Sem BOM, de propósito. <c>Encoding.UTF8</c> escreve BOM e ele apareceria colado na
    /// primeira linha do JSONL — o mesmo defeito já corrigido no AuditLogService.
    /// </summary>
    private static readonly UTF8Encoding SemBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Sem escapar acentos: o arquivo é para o usuário abrir e ler, não só para a máquina.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    /// <param name="sessionId">Identificador da sessão. Vira nome de pasta.</param>
    /// <param name="rootOverride">Raiz alternativa. Existe para o teste não escrever no ~/.AIB real.</param>
    public SessionMemory(string sessionId, string? rootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId vazio", nameof(sessionId));

        // GetFileName impede que um id venha com "..\.." e escreva fora da pasta de memória.
        string seguro = Path.GetFileName(sessionId.Trim());
        if (string.IsNullOrEmpty(seguro)) throw new ArgumentException("sessionId inválido", nameof(sessionId));

        SessionId = seguro;
        string raiz = rootOverride ?? DirectoryService.MemoryDir;
        SessionDir = Path.Combine(raiz, "sessions", seguro);
    }

    public string SessionId { get; }

    public string SessionDir { get; }

    public string RawPath => Path.Combine(SessionDir, "raw.jsonl");

    /// <summary>
    /// Id de sessão a partir de um instante: <c>20260821-143005-812</c>. Os milissegundos
    /// entram porque duas sessões abertas no mesmo segundo (reset logo após abrir o app)
    /// escreveriam na mesma pasta e misturariam dois raw.jsonl.
    /// </summary>
    public static string SessionIdFrom(DateTime instante) =>
        instante.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

    /// <summary>
    /// Grava um turno como uma linha de <c>raw.jsonl</c>.
    /// <para>
    /// Nunca lança: falha de disco não pode derrubar a conversa. Devolve false e registra no
    /// console — a memória é um acréscimo, e um acréscimo que quebra o principal não vale.
    /// </para>
    /// </summary>
    public bool AppendTurn(Turn turn)
    {
        if (turn == null || turn.Messages.Count == 0) return false;

        try
        {
            var registro = new TurnRecord(
                turn.Index,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                turn.Messages.Select(ToRecord).ToList(),
                ArtifactExtractor.Extract(turn));

            string linha = JsonSerializer.Serialize(registro, Json);

            lock (_gate)
            {
                Directory.CreateDirectory(SessionDir);
                File.AppendAllText(RawPath, linha + Environment.NewLine, SemBom);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao gravar turno {turn.Index}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Lê os turnos gravados. Linha corrompida é pulada, não derruba a leitura: o arquivo é
    /// append-only e uma queda de energia no meio de uma escrita deixa exatamente uma linha
    /// truncada — perder o arquivo inteiro por causa dela seria o pior desfecho possível.
    /// </summary>
    public IReadOnlyList<TurnRecord> ReadTurns()
    {
        var turnos = new List<TurnRecord>();
        if (!File.Exists(RawPath)) return turnos;

        try
        {
            foreach (var linha in File.ReadLines(RawPath, SemBom))
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;
                try
                {
                    var registro = JsonSerializer.Deserialize<TurnRecord>(linha, Json);
                    if (registro != null) turnos.Add(registro);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[MEMORIA] Linha ilegível em raw.jsonl, pulada: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao ler raw.jsonl: {ex.Message}");
        }

        return turnos;
    }

    private static MessageRecord ToRecord(ChatMessage message)
    {
        string texto = Turn.TextOf(message);

        return message switch
        {
            UserChatMessage => new MessageRecord("user", texto),

            AssistantChatMessage a when a.ToolCalls is { Count: > 0 } =>
                new MessageRecord("assistant", texto,
                    a.ToolCalls
                        .Where(c => c?.Id != null)
                        .Select(c => new ToolCallRecord(c.Id, c.FunctionName ?? "", c.FunctionArguments?.ToString() ?? ""))
                        .ToList()),

            AssistantChatMessage => new MessageRecord("assistant", texto),

            ToolChatMessage t => new MessageRecord("tool", texto, null, t.ToolCallId),

            SystemChatMessage => new MessageRecord("system", texto),

            _ => new MessageRecord("unknown", texto)
        };
    }
}
