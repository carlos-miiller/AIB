using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace AIB.Services.Tools;

/// <summary>
/// Lembretes únicos: "me lembra às 15h de ligar para o fornecedor", "daqui a 40 minutos",
/// "amanhã às 9h". Cria, lista e cancela.
/// <para>
/// O texto da entrega é escrito pela persona AGORA, na voz dela, e entregue como está pela
/// agenda do App: disparar não chama o modelo. Recorrente ficou de fora por decisão do usuário
/// (regras de calendário, PC desligado em várias ocorrências, texto repetido).
/// </para>
/// <para>
/// Só da boca do usuário (<see cref="ITool.SoComFalaDoUsuario"/>): um e-mail com "me lembre de
/// pagar o boleto X" agendaria a fala de um estranho na voz da persona.
/// </para>
/// </summary>
public sealed class LembreteTool : ITool
{
    /// <summary>Mais longe que isso é agenda, não lembrete.</summary>
    public static readonly TimeSpan Horizonte = TimeSpan.FromDays(366);

    private readonly Lembretes _lembretes;
    private readonly Func<DateTime> _agora;

    /// <param name="lembretes">Os pendentes. Os ensaios passam um com raiz temporária.</param>
    /// <param name="agora">Relógio local. Os ensaios fixam a hora.</param>
    public LembreteTool(Lembretes? lembretes = null, Func<DateTime>? agora = null)
    {
        _lembretes = lembretes ?? new Lembretes();
        _agora = agora ?? (() => DateTime.Now);
    }

    public string Name => Ferramentas.Lembrete;

    public string Description =>
        "Lembrete único que o usuário pediu. create: 'at' (\"HH:mm\" ou \"yyyy-MM-dd HH:mm\") ou "
        + "'in_minutes', e 'text', a sua fala na hora do aviso. list mostra os pendentes; cancel "
        + "tira pelo 'id'.";

    public int RequiredLevel => 1;

    public bool SoComFalaDoUsuario => true;

    public ChatTool ChatToolDefinition => ChatTool.CreateFunctionTool(
        functionName: Name,
        functionDescription: Description,
        functionParameters: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "action": { "type": "string", "enum": ["create", "list", "cancel"] },
                "at": { "type": "string", "description": "Hora local: \"15:00\" (hoje, ou amanhã se já passou) ou \"2026-10-07 09:00\"." },
                "in_minutes": { "type": "integer", "description": "Daqui a quantos minutos, no lugar de 'at'." },
                "text": { "type": "string", "description": "O que você vai dizer na hora, na sua voz, falando com o usuário." },
                "subject": { "type": "string", "description": "Do que se trata, em poucas palavras, para a lista." },
                "id": { "type": "string", "description": "Para cancel: o id que list mostra." }
            },
            "required": ["action"]
        }
        """)
    );

    private sealed record Args(string Acao, string? Em, int? Minutos, string Texto, string Assunto, string Id);

    private static Args Ler(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new Args("", null, null, "", "", "");

            string S(string nome) =>
                r.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

            int? minutos = null;
            if (r.TryGetProperty("in_minutes", out var m))
            {
                if (m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out int n)) minutos = n;
                else if (m.ValueKind == JsonValueKind.String && int.TryParse(m.GetString(), out int s)) minutos = s;
            }

            string em = S("at");
            return new Args(S("action").ToLowerInvariant(), em.Length == 0 ? null : em, minutos, S("text"), S("subject"), S("id"));
        }
        catch (JsonException)
        {
            return new Args("", null, null, "", "", "");
        }
    }

    /// <summary>
    /// A hora local pedida, ou o motivo de não haver uma. "HH:mm" que já passou hoje é amanhã:
    /// "me lembra às 8h" dito às 22h quer dizer a manhã seguinte.
    /// </summary>
    public static (DateTime? Quando, string? Erro) Resolver(string? em, int? minutos, DateTime agora)
    {
        if (minutos is int n)
        {
            if (n <= 0) return (null, "'in_minutes' tem de ser maior que zero.");
            if (TimeSpan.FromMinutes(n) > Horizonte) return (null, "Mais de um ano à frente: longe demais para um lembrete.");
            return (agora.AddMinutes(n), null);
        }

        if (string.IsNullOrWhiteSpace(em)) return (null, "Falta a hora: 'at' ou 'in_minutes'.");

        var br = CultureInfo.InvariantCulture;
        if (TimeSpan.TryParseExact(em, new[] { @"h\:mm", @"hh\:mm" }, br, out var hora))
        {
            var hoje = agora.Date + hora;
            return (hoje > agora ? hoje : hoje.AddDays(1), null);
        }

        if (DateTime.TryParseExact(em, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss" },
                                   br, DateTimeStyles.None, out var data))
        {
            if (data <= agora) return (null, $"{data:dd/MM HH:mm} já passou (agora são {agora:dd/MM HH:mm}).");
            if (data - agora > Horizonte) return (null, "Mais de um ano à frente: longe demais para um lembrete.");
            return (data, null);
        }

        return (null, $"Não entendi a hora \"{em}\". Use \"HH:mm\" ou \"yyyy-MM-dd HH:mm\".");
    }

    public string? Validar(string argumentsJson)
    {
        var a = Ler(argumentsJson);

        switch (a.Acao)
        {
            case "list":
                return null;

            case "cancel":
                return a.Id.Length == 0 ? "ERRO: cancel precisa do 'id' (veja com list)." : null;

            case "create":
                if (a.Texto.Length == 0) return "ERRO: falta 'text', a sua fala na hora do lembrete.";
                var (_, erro) = Resolver(a.Em, a.Minutos, _agora());
                return erro == null ? null : "ERRO: " + erro;

            default:
                return "ERRO: 'action' é create, list ou cancel.";
        }
    }

    /// <summary>Listar só lê o que já foi agendado: não precisa do usuário para confirmar.</summary>
    public bool PedeFalaDoUsuario(string argumentsJson) => Ler(argumentsJson).Acao != "list";

    /// <summary>
    /// O cartão que o registry mostra quando há texto de terceiros no contexto: a hora e a fala
    /// inteira, ou o lembrete que sai. Sem "sempre".
    /// </summary>
    public CommandConfirmationContext? BuildConfirmationContext(string argumentsJson, int userLevel)
    {
        var a = Ler(argumentsJson);
        string? comando = null;

        if (a.Acao == "cancel" && a.Id.Length > 0)
        {
            comando = $"CANCELAR LEMBRETE {a.Id}";
        }
        else if (a.Acao == "create" && a.Texto.Length > 0)
        {
            var (quando, _) = Resolver(a.Em, a.Minutos, _agora());
            if (quando is DateTime q) comando = $"AGENDAR LEMBRETE para {q:dd/MM HH:mm}: \"{a.Texto}\"";
        }

        if (comando == null) return null;

        return new CommandConfirmationContext { Tool = Name, Command = comando, Level = userLevel, SemSempre = true };
    }

    public Task<string> ExecuteAsync(string argumentsJson, int userLevel = 1)
    {
        var a = Ler(argumentsJson);
        var agora = _agora();

        return Task.FromResult(a.Acao switch
        {
            "list" => Listar(agora),
            "cancel" => _lembretes.Cancelar(a.Id)
                ? $"Lembrete {a.Id} cancelado."
                : $"ERRO: não há lembrete com id '{a.Id}'. Veja com list.",
            _ => Criar(a, agora)
        });
    }

    private string Criar(Args a, DateTime agora)
    {
        var (quando, _) = Resolver(a.Em, a.Minutos, agora);
        string assunto = a.Assunto.Length > 0 ? a.Assunto : Resumo(a.Texto);

        var novo = _lembretes.Criar(quando!.Value.ToUniversalTime(), a.Texto, assunto);
        if (novo == null)
            return $"ERRO: já há {Lembretes.Teto} lembretes pendentes. Peça ao usuário para cancelar algum.";

        return $"Agendado (id {novo.Id}) para {Quando(quando.Value, agora)}: {assunto}. "
               + "O aviso chega pelo orbe, ou pela bandeja com ele desligado.";
    }

    private string Listar(DateTime agora)
    {
        var todos = _lembretes.Listar();
        if (todos.Count == 0) return "Nenhum lembrete pendente.";

        var sb = new StringBuilder($"{todos.Count} lembrete(s) pendente(s):");
        foreach (var l in todos)
            sb.Append($"\n- {l.Id}: {Quando(l.QuandoUtc.ToLocalTime(), agora)} — {l.Assunto}");
        return sb.ToString();
    }

    /// <summary>"hoje às 15:00", "amanhã às 09:00", "qui, 09/10 às 14:30".</summary>
    public static string Quando(DateTime local, DateTime agora)
    {
        string hora = local.ToString("HH:mm");
        if (local.Date == agora.Date) return $"hoje às {hora}";
        if (local.Date == agora.Date.AddDays(1)) return $"amanhã às {hora}";
        return local.ToString("ddd, dd/MM", new CultureInfo("pt-BR")) + $" às {hora}";
    }

    private static string Resumo(string texto)
    {
        string linha = texto.Split('\n').First().Trim();
        return linha.Length <= 60 ? linha : linha[..57] + "...";
    }
}
