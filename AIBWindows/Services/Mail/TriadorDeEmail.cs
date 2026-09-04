using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services.Ai;
using OpenAI.Chat;

namespace AIB.Services.Mail;

/// <summary>O que o modelo disse sobre uma mensagem.</summary>
/// <param name="Uid">A mensagem a que o veredito pertence.</param>
/// <param name="Urgencia">Quanto ela pede atenção.</param>
/// <param name="Resumo">O que estão pedindo, e até quando.</param>
public readonly record struct VereditoDeEmail(uint Uid, MailUrgency Urgencia, string Resumo);

/// <summary>
/// Degrau 3 do funil — o 9B lê o lote que sobrou e diz, de cada mensagem, o que estão pedindo e
/// até quando.
/// <para>
/// Chamada FORA DE BANDA, como a do <see cref="ChatTitler"/> e a do compactador: lista de
/// ferramentas vazia, temperatura zero, sem raciocínio. Não passa pela conversa, e é isso que
/// mantém a REGRA 3 do vigia de pé — conteúdo de e-mail não entra no <c>raw.jsonl</c> nem vira
/// capítulo. O que atravessa é o veredito, que não carrega corpo nem remetente.
/// </para>
/// <para>
/// Um lote só, e não uma chamada por mensagem: numa máquina de ~34 tok/s de prefill, trinta
/// chamadas pagariam trinta vezes o prompt de sistema.
/// </para>
/// </summary>
public sealed class TriadorDeEmail
{
    /// <summary>Teto de tokens da resposta. Trinta linhas curtas de JSON cabem folgadas.</summary>
    public const int TetoDeResposta = 1400;

    private static readonly ChatRequestOptions Opcoes =
        new(Temperature: 0.0f, Think: false, NumPredict: TetoDeResposta);

    private const string Prompt =
        """
        Você tria a caixa de entrada de um profissional ocupado, em Português (Brasil).

        Recebe uma lista numerada de mensagens. Para CADA uma, responda o que estão pedindo a
        ele e até quando — não descreva o assunto, diga a AÇÃO esperada.

        Responda APENAS um array JSON, sem cercas de código e sem texto antes ou depois:
        [{"uid":123,"urgencia":"maxima","resumo":"..."}]

        Campos:
        - uid: o número que veio entre colchetes na mensagem. Copie exatamente.
        - urgencia: "maxima", "media" ou "baixa".
            maxima = tem prazo hoje ou amanhã, ou alguém está bloqueado esperando ele.
            media  = pede resposta, sem prazo curto.
            baixa  = informativo; ele pode ler depois sem prejuízo.
        - resumo: UMA frase, no máximo 20 palavras, sem repetir o assunto literal.

        Uma entrada para cada mensagem recebida. Sem inventar mensagem que não veio.
        Na dúvida entre dois níveis, escolha o MAIOR.
        """;

    private readonly IChatProvider _provider;

    public TriadorDeEmail(IChatProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<IReadOnlyList<VereditoDeEmail>> TriarAsync(
        IReadOnlyList<MensagemDeEmail> lote, CancellationToken ct = default)
    {
        if (lote == null || lote.Count == 0) return Array.Empty<VereditoDeEmail>();

        var mensagens = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(Prompt),
            ChatMessage.CreateUserMessage(Montar(lote))
        };

        var resultado = await _provider
            .CompleteAsync(mensagens, Array.Empty<ChatTool>(), Opcoes, ct)
            .ConfigureAwait(false);

        return Interpretar(resultado.Text);
    }

    /// <summary>
    /// O texto que o modelo recebe. Público para os ensaios: é aqui que se confere que nada
    /// além do necessário desce para o prompt.
    /// </summary>
    public static string Montar(IReadOnlyList<MensagemDeEmail> lote)
    {
        var sb = new StringBuilder();

        foreach (var m in lote)
        {
            sb.Append('[').Append(m.Uid).Append("] de: ").Append(m.NomeDoRemetente)
              .Append(" <").Append(m.De).Append('>').AppendLine();
            sb.Append("assunto: ").Append(m.Assunto).AppendLine();
            sb.Append("recebida: ").Append(m.RecebidaUtc.ToLocalTime().ToString("dd/MM HH:mm")).AppendLine();
            if (m.Corpo.Length > 0) sb.AppendLine(m.Corpo);
            sb.AppendLine("---");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tira os vereditos do que o modelo devolveu.
    /// <para>
    /// Função pura e separada porque é ela que erra: o modelo põe cerca de código, escreve uma
    /// frase antes do array, inventa um nível de urgência que não existe, devolve o uid como
    /// texto. Testar isso com o Ollama na frente seria testar o modelo; aqui se testa a
    /// leitura.
    /// </para>
    /// <para>
    /// O que não dá para ler é DESCARTADO, não chutado. Quem cuida das mensagens sem veredito é
    /// o chamador, que as devolve com um resumo de código — inventar urgência aqui seria a
    /// alucinação virando prioridade na tela.
    /// </para>
    /// </summary>
    public static IReadOnlyList<VereditoDeEmail> Interpretar(string? bruto)
    {
        string? json = RecortarArray(bruto);
        if (json == null) return Array.Empty<VereditoDeEmail>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<VereditoDeEmail>();

            var vereditos = new List<VereditoDeEmail>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!LerUid(item, out uint uid)) continue;

                string resumo = Texto(item, "resumo", "summary", "acao", "ação");
                if (resumo.Length == 0) continue;

                string urgencia = Texto(item, "urgencia", "urgência", "urgency", "prioridade");

                vereditos.Add(new VereditoDeEmail(uid, Nivel(urgencia), resumo));
            }

            return vereditos;
        }
        catch (JsonException)
        {
            return Array.Empty<VereditoDeEmail>();
        }
    }

    /// <summary>
    /// Acha o array dentro do que veio. O modelo costuma embrulhar em cerca de código, ou
    /// escrever uma linha de apresentação antes dele.
    /// </summary>
    private static string? RecortarArray(string? bruto)
    {
        string texto = (bruto ?? "").Trim();
        if (texto.Length == 0) return null;

        int abre = texto.IndexOf('[');
        int fecha = texto.LastIndexOf(']');

        return abre >= 0 && fecha > abre ? texto.Substring(abre, fecha - abre + 1) : null;
    }

    private static bool LerUid(JsonElement item, out uint uid)
    {
        uid = 0;
        if (!item.TryGetProperty("uid", out var campo)) return false;

        if (campo.ValueKind == JsonValueKind.Number) return campo.TryGetUInt32(out uid);
        return campo.ValueKind == JsonValueKind.String && uint.TryParse(campo.GetString(), out uid);
    }

    /// <summary>
    /// O primeiro dos nomes que existir e for texto.
    /// <para>
    /// Aceita sinônimo porque o modelo troca o nome do campo. Visto em produção: no meio de
    /// catorze objetos certos veio um <c>"urgency"</c> em inglês, o campo "faltou", a leitura
    /// caiu no padrão MÉDIA da regra 5 e um anúncio da Netflix foi parar na tela. O prompt pede
    /// <c>urgencia</c>; ler o sinônimo custa nada e não afrouxa nada.
    /// </para>
    /// </summary>
    private static string Texto(JsonElement item, params string[] nomes)
    {
        foreach (string nome in nomes)
        {
            if (item.TryGetProperty(nome, out var campo) && campo.ValueKind == JsonValueKind.String)
            {
                string valor = (campo.GetString() ?? "").Trim();
                if (valor.Length > 0) return valor;
            }
        }

        return "";
    }

    /// <summary>
    /// Nível a partir do que o modelo escreveu. Palavra desconhecida vira MÉDIA, e não baixa:
    /// regra 5 do vigia, enviesado para recall — na dúvida, sinaliza.
    /// </summary>
    public static MailUrgency Nivel(string? texto)
    {
        string t = (texto ?? "").Trim().ToLowerInvariant();

        if (t.StartsWith("max") || t.StartsWith("máx") || t.StartsWith("alta") || t.StartsWith("urgen"))
            return MailUrgency.Maxima;

        if (t.StartsWith("baix") || t.StartsWith("low") || t.StartsWith("inform"))
            return MailUrgency.Baixa;

        return MailUrgency.Media;
    }
}
