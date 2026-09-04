using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AIB.Services.Mail;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Deixa a conversa consultar o que a triagem automática já fez.
/// <para>
/// SOMENTE LEITURA, e nem sequer do servidor: ela lê o diário em disco, escrito pelo vigia
/// quando ele rodou. Não conecta, não busca, não marca, não responde e não apaga. Uma pergunta
/// como "tem algo urgente hoje?" custa um acesso a arquivo, e não uma conexão IMAP com uma
/// triagem de vários minutos atrás dela.
/// </para>
/// <para>
/// A consequência disso é a que precisa ficar explícita para o modelo, e está na descrição da
/// ferramenta: ela responde sobre o que JÁ FOI TRIADO, e não sobre a caixa agora. Se o digest
/// das 12h55 ainda não rodou, o e-mail que chegou 12h50 não está aqui — e dizer "não há nada
/// urgente" nesse caso seria mentira. Por isso toda resposta abre dizendo quando foi a última
/// passada.
/// </para>
/// <para>
/// REGRA 3: o que sai daqui é veredito — remetente, assunto, urgência e a frase do resumo. O
/// corpo da mensagem não existe no diário, então não há como ele escapar por este caminho. O
/// resto do que sai daqui ENTRA no raw.jsonl junto com a conversa, e é essa a troca que o
/// mostrador <c>MailJournalDays</c> controla.
/// </para>
/// </summary>
public sealed class ConsultarEmailsTool : ITool
{
    /// <summary>Teto de mensagens listadas numa resposta. Acima disso vira contagem.</summary>
    public const int TetoDaLista = 15;

    private readonly SettingsService? _settings;
    private readonly DiarioDeTriagem _diario;
    private readonly Func<DateTime> _agora;

    public ConsultarEmailsTool(
        SettingsService? settings = null,
        DiarioDeTriagem? diario = null,
        Func<DateTime>? agora = null)
    {
        _settings = settings;
        _diario = diario ?? new DiarioDeTriagem();
        _agora = agora ?? (() => DateTime.Now);
    }

    public string Name => "consultar_emails";

    public string Description =>
        "Consulta o que a triagem automática de e-mail já leu e classificou. Use para perguntas " +
        "como 'quantos e-mails foram tratados hoje', 'tem algo urgente', 'chegou algo do fulano'. " +
        "Lê o registro das triagens que já rodaram — NÃO acessa a caixa agora, então não sabe de " +
        "mensagens chegadas depois da última passada.";

    public int RequiredLevel => 1;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "periodo": {
                    "type": "string",
                    "enum": ["hoje", "ontem", "semana"],
                    "description": "Que intervalo consultar. Padrão: hoje."
                },
                "urgencia": {
                    "type": "string",
                    "enum": ["maxima", "media", "baixa", "todas"],
                    "description": "Filtra por nível. Padrão: todas."
                },
                "remetente": {
                    "type": "string",
                    "description": "Filtra por parte do nome ou do endereço de quem mandou."
                },
                "assunto": {
                    "type": "string",
                    "description": "Filtra por parte do assunto."
                }
            }
        }
        """)
    );

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        var config = _settings?.LoadSettings();

        if (config != null && !config.ShadowHandlesMail)
            return Task.FromResult(
                "A triagem automática de e-mail está DESLIGADA nas configurações (Shadow > tratar " +
                "e-mails). Nada foi lido, então não há o que consultar.");

        if (config != null && config.MailJournalDays <= 0)
            return Task.FromResult(
                "O registro das triagens está desligado (Configurações > E-mail > dias de diário = 0). " +
                "A triagem roda e avisa na hora, mas não guarda nada para consulta posterior.");

        if (config != null && (config.MailAccounts == null || config.MailAccounts.Count == 0))
            return Task.FromResult("Nenhuma caixa de e-mail configurada.");

        var (periodo, urgencia, remetente, assunto) = LerArgumentos(argumentsJson);
        var (de, ate, rotulo) = Intervalo(periodo, _agora());

        var passadas = _diario.LerPeriodo(de, ate);

        Console.WriteLine($"[TOOL: consultar_emails] {rotulo}, urgência={urgencia}, " +
                          $"{passadas.Count} passada(s) no registro.");

        return Task.FromResult(Responder(passadas, rotulo, urgencia, remetente, assunto));
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Monta a resposta. Função separada e pura porque é ela que os ensaios cobrem: o valor
    /// desta ferramenta está em NÃO deixar o modelo inventar número, e o texto abaixo é o que
    /// decide isso.
    /// </summary>
    public static string Responder(
        IReadOnlyList<PassadaAnotada> passadas,
        string rotuloDoPeriodo,
        string urgencia = "todas",
        string? remetente = null,
        string? assunto = null)
    {
        passadas ??= Array.Empty<PassadaAnotada>();

        if (passadas.Count == 0)
            return $"Nenhuma triagem registrada {rotuloDoPeriodo}. Os digests rodam às " +
                   $"{string.Join(", ", AgendaDoVigia.Horarios.Select(h => h.ToString(@"hh\:mm")))}.";

        var todos = passadas.SelectMany(p => p.Triados ?? Array.Empty<EmailTriado>()).ToList();

        int lidas = passadas.Sum(p => p.Lidas);
        int descartadas = passadas.Sum(p => p.Descartadas);

        var ultima = passadas
            .Select(p => DiarioDeTriagem.Quando(p.QuandoUtc).ToLocalTime())
            .DefaultIfEmpty(DateTime.Now)
            .Max();

        var sb = new StringBuilder();

        sb.AppendLine($"Triagem {rotuloDoPeriodo}: {passadas.Count} passada(s), {lidas} mensagem(ns) " +
                      $"lida(s), {descartadas} descartada(s) pelo filtro barato, {todos.Count} " +
                      $"efetivamente triada(s) pelo modelo.");

        sb.AppendLine($"Por urgência: {Conta(todos, "maxima")} máxima, {Conta(todos, "media")} média, " +
                      $"{Conta(todos, "baixa")} baixa.");

        sb.AppendLine($"Última passada: {ultima:dd/MM HH:mm}. Mensagens chegadas depois disso ainda " +
                      "não foram lidas.");

        var filtrados = Filtrar(todos, urgencia, remetente, assunto);

        string filtro = DescreverFiltro(urgencia, remetente, assunto);

        if (filtrados.Count == 0)
        {
            sb.AppendLine();
            sb.Append(filtro.Length == 0
                ? "Nenhuma mensagem triada no período."
                : $"Nenhuma mensagem triada {filtro}.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine();
        sb.AppendLine(filtro.Length == 0
            ? $"As {filtrados.Count} triada(s):"
            : $"{filtrados.Count} {filtro}:");

        foreach (var e in filtrados.Take(TetoDaLista))
            sb.AppendLine($"- [{e.Urgencia}] {e.Assunto} — {Quem(e)} — {e.Resumo}");

        if (filtrados.Count > TetoDaLista)
            sb.AppendLine($"(+{filtrados.Count - TetoDaLista} não listada(s))");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Ordena por urgência e depois por chegada, mais recente primeiro. A ordem importa: com o
    /// teto da lista, o que fica de fora tem de ser sempre o menos urgente.
    /// </summary>
    public static IReadOnlyList<EmailTriado> Filtrar(
        IEnumerable<EmailTriado> triados, string urgencia, string? remetente, string? assunto)
    {
        return (triados ?? Array.Empty<EmailTriado>())
            .Where(e => Casa(e.Urgencia, urgencia))
            .Where(e => Contem(e.Remetente, remetente) || Contem(e.Nome, remetente))
            .Where(e => Contem(e.Assunto, assunto))
            .OrderByDescending(e => Peso(e.Urgencia))
            .ThenByDescending(e => DiarioDeTriagem.Quando(e.RecebidaUtc))
            .ToList();
    }

    /// <summary>Os dois extremos do período pedido, e como chamá-lo em português.</summary>
    public static (DateTime De, DateTime Ate, string Rotulo) Intervalo(string? periodo, DateTime agora)
    {
        return (periodo ?? "").Trim().ToLowerInvariant() switch
        {
            "ontem" => (agora.Date.AddDays(-1), agora.Date.AddDays(-1), "de ontem"),
            "semana" or "7d" => (agora.Date.AddDays(-6), agora.Date, "dos últimos 7 dias"),
            _ => (agora.Date, agora.Date, "de hoje")
        };
    }

    private static (string Periodo, string Urgencia, string? Remetente, string? Assunto) LerArgumentos(
        string? argumentsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return ("hoje", "todas", null, null);

            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            if (args.ValueKind != JsonValueKind.Object) return ("hoje", "todas", null, null);

            return (Texto(args, "periodo") ?? "hoje",
                    Texto(args, "urgencia") ?? "todas",
                    Texto(args, "remetente"),
                    Texto(args, "assunto"));
        }
        catch
        {
            // Argumento ilegível não pode virar erro: a pergunta sem filtro nenhum é a mais
            // comum, e "quantos e-mails hoje" não precisa de argumento algum.
            return ("hoje", "todas", null, null);
        }
    }

    private static string? Texto(JsonElement args, string nome) =>
        args.TryGetProperty(nome, out var campo) && campo.ValueKind == JsonValueKind.String
            ? campo.GetString()
            : null;

    private static int Conta(IEnumerable<EmailTriado> triados, string nivel) =>
        triados.Count(e => string.Equals(e.Urgencia, nivel, StringComparison.OrdinalIgnoreCase));

    private static bool Casa(string? nivel, string? pedido)
    {
        string p = (pedido ?? "todas").Trim().ToLowerInvariant();
        return p is "todas" or ""
            || string.Equals(nivel, p, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contem(string? campo, string? agulha) =>
        string.IsNullOrWhiteSpace(agulha)
        || (campo ?? "").Contains(agulha.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Quem(EmailTriado e) =>
        string.IsNullOrWhiteSpace(e.Nome) || e.Nome == e.Remetente
            ? e.Remetente
            : $"{e.Nome} <{e.Remetente}>";

    private static string DescreverFiltro(string urgencia, string? remetente, string? assunto)
    {
        var partes = new List<string>();

        string nivel = (urgencia ?? "").Trim().ToLowerInvariant();
        if (nivel is "maxima" or "media" or "baixa")
            partes.Add("de urgência " + (nivel switch
            {
                "maxima" => "máxima",
                "media" => "média",
                _ => "baixa"
            }));

        if (!string.IsNullOrWhiteSpace(remetente)) partes.Add($"de \"{remetente.Trim()}\"");
        if (!string.IsNullOrWhiteSpace(assunto)) partes.Add($"com \"{assunto.Trim()}\" no assunto");

        return partes.Count == 0 ? "" : string.Join(", ", partes);
    }

    private static int Peso(string? urgencia) => (urgencia ?? "").ToLowerInvariant() switch
    {
        "maxima" => 2,
        "media" => 1,
        _ => 0
    };
}
