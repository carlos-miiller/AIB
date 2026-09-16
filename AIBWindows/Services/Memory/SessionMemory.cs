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
/// inconsistência de enredo — gera agente agindo sobre informação errada com shell na
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

    public string ChaptersPath => Path.Combine(SessionDir, "chapters.jsonl");

    public string ActsPath => Path.Combine(SessionDir, "acts.jsonl");

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
    public bool AppendTurn(Turn turn, string? id = null)
    {
        if (turn == null || turn.Messages.Count == 0) return false;

        try
        {
            return AppendRecord(ParaRegistro(turn, id));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao gravar turno {turn.Index}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Grava um turno já em forma de registro. Mesma política: nunca lança.</summary>
    public bool AppendRecord(TurnRecord registro)
    {
        try
        {
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
            Console.WriteLine($"[MEMORIA] Falha ao gravar turno {registro.Index}: {ex.Message}");
            return false;
        }
    }

    private static TurnRecord ParaRegistro(Turn turn, string? id) =>
        new(
            turn.Index,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            turn.Messages.Select(ToRecord).ToList(),
            ArtifactExtractor.Extract(turn),
            id);

    /// <summary>
    /// O turno ainda em curso, regravado inteiro a cada passo. Um arquivo só, e não uma linha
    /// por passo: o que importa dele é o estado mais recente, e ele some quando o turno fecha.
    /// </summary>
    public string TurnoAbertoPath => Path.Combine(SessionDir, "turno-aberto.json");

    /// <summary>
    /// Regrava o turno em curso. Escreve num temporário e troca: uma queda no meio da escrita
    /// deixa o estado anterior inteiro, e não um JSON cortado. Nunca lança.
    /// </summary>
    public void GravarTurnoAberto(Turn turn, string id)
    {
        try
        {
            string json = JsonSerializer.Serialize(ParaRegistro(turn, id), Json);

            lock (_gate)
            {
                Directory.CreateDirectory(SessionDir);
                string temporario = TurnoAbertoPath + ".tmp";
                File.WriteAllText(temporario, json, SemBom);
                File.Move(temporario, TurnoAbertoPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao guardar o turno em curso: {ex.Message}");
        }
    }

    /// <summary>O turno que ficou em curso, ou nulo. Arquivo ilegível conta como ausente.</summary>
    public TurnRecord? LerTurnoAberto()
    {
        try
        {
            if (!File.Exists(TurnoAbertoPath)) return null;
            return JsonSerializer.Deserialize<TurnRecord>(File.ReadAllText(TurnoAbertoPath, SemBom), Json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Turno em curso ilegível, ignorado: {ex.Message}");
            return null;
        }
    }

    /// <summary>Some com o arquivo do turno em curso. Nunca lança.</summary>
    public void ApagarTurnoAberto()
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(TurnoAbertoPath)) File.Delete(TurnoAbertoPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao apagar o turno em curso: {ex.Message}");
        }
    }

    /// <summary>
    /// Leva ao <c>raw.jsonl</c> o turno que ficou em curso quando o AIB caiu, fechado com
    /// <paramref name="marca"/>, e apaga o arquivo dele. Devolve se gravou.
    /// <para>
    /// Não grava de novo o que já está lá: a queda pode ter vindo DEPOIS de o turno chegar ao
    /// arquivo e antes de o arquivo de recuperação ser apagado. A identidade do turno decide.
    /// </para>
    /// </summary>
    public bool RecuperarTurnoAberto(string marca)
    {
        // Ilegível fica onde está: apagar seria perder de vez o único rastro do turno.
        var aberto = LerTurnoAberto();
        if (aberto == null) return false;

        var gravados = ReadTurns();
        bool jaEsta = aberto.Id != null && gravados.Any(t => t.Id == aberto.Id);

        bool gravou = false;
        if (!jaEsta && aberto.Messages.Count > 0)
        {
            var mensagens = aberto.Messages.ToList();
            var ultima = mensagens[^1];
            bool fecha = ultima.Role == "assistant" && (ultima.ToolCalls == null || ultima.ToolCalls.Count == 0);
            if (!fecha) mensagens.Add(new MessageRecord("assistant", marca));

            gravou = AppendRecord(aberto with
            {
                Index = gravados.Count == 0 ? 0 : gravados[^1].Index + 1,
                Messages = mensagens
            });
        }

        if (jaEsta || gravou) ApagarTurnoAberto();
        return gravou;
    }

    /// <summary>
    /// Lê os turnos gravados. Linha corrompida é pulada, não derruba a leitura: o arquivo é
    /// append-only e uma queda de energia no meio de uma escrita deixa exatamente uma linha
    /// truncada — perder o arquivo inteiro por causa dela seria o pior desfecho possível.
    /// </summary>
    public IReadOnlyList<TurnRecord> ReadTurns() => ReadLines<TurnRecord>(RawPath);

    /// <summary>
    /// Grava um capítulo fechado. Mesma política do turno: append-only e nunca lança.
    /// </summary>
    public bool AppendChapter(Chapter chapter)
    {
        if (chapter == null) return false;

        try
        {
            string linha = JsonSerializer.Serialize(chapter, Json);

            lock (_gate)
            {
                Directory.CreateDirectory(SessionDir);
                File.AppendAllText(ChaptersPath, linha + Environment.NewLine, SemBom);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao gravar capítulo {chapter.Index}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Capítulos gravados, na ordem. Linha ilegível é pulada.</summary>
    public IReadOnlyList<Chapter> ReadChapters() => ReadLines<Chapter>(ChaptersPath);

    /// <summary>
    /// Grava um ato fechado. Os capítulos que ele resume CONTINUAM em chapters.jsonl: o ato
    /// substitui os capítulos no prompt, não em disco. Em disco nada é substituído.
    /// </summary>
    public bool AppendAct(Act act)
    {
        if (act == null) return false;

        try
        {
            string linha = JsonSerializer.Serialize(act, Json);

            lock (_gate)
            {
                Directory.CreateDirectory(SessionDir);
                File.AppendAllText(ActsPath, linha + Environment.NewLine, SemBom);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao gravar ato {act.Index}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Atos gravados, na ordem. Linha ilegível é pulada.</summary>
    public IReadOnlyList<Act> ReadActs() => ReadLines<Act>(ActsPath);

    private List<T> ReadLines<T>(string caminho) where T : class
    {
        var itens = new List<T>();
        if (!File.Exists(caminho)) return itens;

        try
        {
            foreach (var linha in File.ReadLines(caminho, SemBom))
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<T>(linha, Json);
                    if (item != null) itens.Add(item);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[MEMORIA] Linha ilegível em {Path.GetFileName(caminho)}, pulada: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORIA] Falha ao ler {Path.GetFileName(caminho)}: {ex.Message}");
        }

        return itens;
    }

    private static MessageRecord ToRecord(ChatMessage message)
    {
        // O corpo de um e-mail lido na conversa vive no contexto vivo e NUNCA aqui: o raw.jsonl
        // não é apagado. Ver ConteudoDeTerceiros.
        string texto = AIB.Services.Mail.ConteudoDeTerceiros.Redigir(Turn.TextOf(message));

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
