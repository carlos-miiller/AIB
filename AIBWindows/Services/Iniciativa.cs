using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIB.Services;

/// <summary>
/// O que a iniciativa aprendeu, para sobreviver ao arranque. Na memória é um estado só; no
/// disco são dois arquivos cifrados (<see cref="ArquivoCifrado"/>): o que é do USUÁRIO fica em
/// <c>~/.AIB/iniciativa.dat</c> (faixas de horário, pausa, sorteio, contagem do dia), e o que é
/// da relação com UM personagem, em <c>~/.AIB/character/&lt;Nome&gt;/vinculo.dat</c>
/// (<see cref="Vinculo"/>). Ver <see cref="Iniciativa.Trocar"/>.
/// </summary>
public sealed class EstadoDaIniciativa
{
    /// <summary>O quanto ele gosta de conversa com ela, em geral. 1 é neutro.</summary>
    public double Geral { get; set; } = 1;

    /// <summary>O mesmo por faixa de 2 h do dia (0 = 0h–2h, 4 = 8h–10h...).</summary>
    public double[] Faixas { get; set; } = Enumerable.Repeat(1.0, Iniciativa.NumeroDeFaixas).ToArray();

    /// <summary>A última fala por iniciativa, enquanto não foi classificada.</summary>
    public DateTime? FalaUtc { get; set; }

    /// <summary>A faixa em que a fala aconteceu: é ela que recebe o que se aprender.</summary>
    public int FaixaDaFala { get; set; }

    /// <summary>Se ele abriu o pulso e leu.</summary>
    public bool Leu { get; set; }

    /// <summary>A primeira resposta dele. Nula: ainda não respondeu, e ela não insiste.</summary>
    public DateTime? RespostaUtc { get; set; }

    /// <summary>Turnos dele na conversa do orbe desde a fala.</summary>
    public int Turnos { get; set; }

    /// <summary>Palavras da primeira resposta.</summary>
    public int Palavras { get; set; }

    /// <summary>
    /// Quando ele começou, por conta própria, a conversa do orbe que ainda está sendo contada.
    /// Nula: não há conversa espontânea aberta.
    /// </summary>
    public DateTime? ConversaUtc { get; set; }

    /// <summary>A faixa em que ele puxou a conversa.</summary>
    public int FaixaDaConversa { get; set; }

    /// <summary>Turnos dele na conversa espontânea.</summary>
    public int TurnosDaConversa { get; set; }

    /// <summary>Depois de um "agora não", ela não puxa assunto até aqui.</summary>
    public DateTime? PausaAteUtc { get; set; }

    /// <summary>Último sorteio, para sortear de 10 em 10 min e não a cada batida.</summary>
    public DateTime? SorteioUtc { get; set; }

    /// <summary>O dia local das contagens e do esquecimento, "yyyy-MM-dd".</summary>
    public string Dia { get; set; } = "";

    public int MensagensHoje { get; set; }

    /// <summary>As últimas mensagens por iniciativa, para ela não repetir. Sobrevive ao arranque.</summary>
    public System.Collections.Generic.List<string> Recentes { get; set; } = new();

    /// <summary>Os últimos ganchos usados, para o sorteio variar o assunto.</summary>
    public System.Collections.Generic.List<string> GanchosRecentes { get; set; } = new();

    /// <summary>Como terminou a última iniciativa, em palavras: "virou conversa", "foi ignorada".</summary>
    public string UltimoDesfecho { get; set; } = "";
}

/// <summary>
/// A parte do estado que é da relação com UM personagem: o quanto o usuário conversa com ele,
/// a fala dele que espera resposta, a conversa que o usuário puxou, o que ele já disse.
/// <para>
/// Era tudo um arquivo só, e trocar de personagem herdava o ritmo que o anterior tinha
/// construído. As faixas de horário NÃO vêm para cá: quando o usuário gosta de ser interrompido
/// não muda com quem fala.
/// </para>
/// </summary>
public sealed class Vinculo
{
    public double Geral { get; set; } = 1;
    public DateTime? FalaUtc { get; set; }
    public int FaixaDaFala { get; set; }
    public bool Leu { get; set; }
    public DateTime? RespostaUtc { get; set; }
    public int Turnos { get; set; }
    public int Palavras { get; set; }
    public DateTime? ConversaUtc { get; set; }
    public int FaixaDaConversa { get; set; }
    public int TurnosDaConversa { get; set; }
    public System.Collections.Generic.List<string> Recentes { get; set; } = new();
    public System.Collections.Generic.List<string> GanchosRecentes { get; set; } = new();
    public string UltimoDesfecho { get; set; } = "";

    public static Vinculo De(EstadoDaIniciativa e) => new()
    {
        Geral = e.Geral,
        FalaUtc = e.FalaUtc,
        FaixaDaFala = e.FaixaDaFala,
        Leu = e.Leu,
        RespostaUtc = e.RespostaUtc,
        Turnos = e.Turnos,
        Palavras = e.Palavras,
        ConversaUtc = e.ConversaUtc,
        FaixaDaConversa = e.FaixaDaConversa,
        TurnosDaConversa = e.TurnosDaConversa,
        Recentes = e.Recentes.ToList(),
        GanchosRecentes = e.GanchosRecentes.ToList(),
        UltimoDesfecho = e.UltimoDesfecho
    };

    public void Para(EstadoDaIniciativa e)
    {
        e.Geral = Geral;
        e.FalaUtc = FalaUtc;
        e.FaixaDaFala = FaixaDaFala;
        e.Leu = Leu;
        e.RespostaUtc = RespostaUtc;
        e.Turnos = Turnos;
        e.Palavras = Palavras;
        e.ConversaUtc = ConversaUtc;
        e.FaixaDaConversa = FaixaDaConversa;
        e.TurnosDaConversa = TurnosDaConversa;
        e.Recentes = (Recentes ?? new()).ToList();
        e.GanchosRecentes = (GanchosRecentes ?? new()).ToList();
        e.UltimoDesfecho = UltimoDesfecho ?? "";
    }
}

/// <summary>
/// O temperamento de um personagem na iniciativa, do <c>info.json</c> dele
/// (<see cref="AgentTemperament"/>). O padrão é o que valia para todos.
/// </summary>
/// <param name="Chance">Multiplica a chance base do sorteio. 1 é o padrão; menos, mais calado.</param>
/// <param name="Apego">
/// O teto do fator de conversa. 1,70 nasceu da alma afetuosa da Ellen; um personagem mais seco
/// sobe menos com a mesma conversa.
/// </param>
public readonly record struct Temperamento(double Chance, double Apego)
{
    public static readonly Temperamento Padrao = new(1, Iniciativa.TetoDaConversa);

    public static Temperamento De(AgentTemperament? t) => t == null
        ? Padrao
        : new(Math.Clamp(t.Initiative <= 0 ? 1 : t.Initiative, 0.1, 3),
              Math.Clamp(t.Attachment <= 0 ? Iniciativa.TetoDaConversa : t.Attachment, 1.10, 2.50));
}

/// <summary>O ponto de partida de uma mensagem por iniciativa, escolhido pelo código.</summary>
/// <param name="Tipo">"pendência" ou "fato".</param>
public sealed record Gancho(string Tipo, string Texto);

/// <summary>
/// A persona puxa assunto sozinha: retoma algo que ficou em aberto, pergunta sobre o usuário.
/// <para>
/// O ALGORITMO DECIDE QUANDO, ELA SÓ ESCREVE — decisão do usuário. De 10 em 10 min, nas batidas
/// em que ela poderia falar, sorteia <c>chance = 1,5% × geral × faixa</c>; acertando, o código
/// escolhe também o gancho (<see cref="EscolherGancho"/>) e o modelo recebe "você está sem
/// fazer nada, ele está online, você decide mandar uma mensagem". Não há saída NADA: cada
/// chamada é uma mensagem, e o custo é o das mensagens. O gancho é o que evita o "oi, como vai?"
/// de quem foi mandado falar sem ter assunto. Sem número fixo por dia e sem intervalo mínimo:
/// às vezes ela fala duas vezes numa manhã e depois some, como gente.
/// </para>
/// <para>
/// OS MULTIPLICADORES SÃO O APRENDIZADO. Cada fala é classificada uma vez — 30 min depois da
/// primeira resposta, ou 8 h sem resposta — e o fator vai inteiro para a faixa de 2 h em que
/// ela falou e pela metade (raiz) para o geral. Conversa sobe até 1,70, porque a alma dela é
/// afetuosa e quem conversa muito com ela a deixa mais inclinada a puxar assunto; ignorar desce
/// 0,75; "agora não" desce 0,5 e cala até o fim do dia ou por 4 h. Todo dia os multiplicadores
/// voltam 10% na direção de 1: uma semana ruim não a cala para sempre.
/// </para>
/// <para>
/// ELE PUXAR CONVERSA TAMBÉM CONTA — pedido do usuário: "que tal nós mandarmos mensagem no
/// shadow e isso também contabilizar no algoritmo?". Mensagem dele no orbe sem fala dela
/// esperando abre uma conversa espontânea de 30 min, que só sobe (<see cref="FatorEspontaneo"/>)
/// e sobe menos que a resposta a uma iniciativa: boa parte do que ele manda ali é pedido de
/// trabalho, não vontade de conversar.
/// </para>
/// <para>
/// Travas que não aprendem: com uma fala sem resposta ela não sorteia; teto de mensagens por dia
/// escolhido pelo usuário (padrão 6; cada uma é uma requisição paga); silêncio, presença e "não
/// perturbe".
/// </para>
/// </summary>
public sealed class Iniciativa
{
    public const int NumeroDeFaixas = 12;

    /// <summary>O teto padrão do fator de conversa (<see cref="Temperamento.Apego"/>).</summary>
    public const double TetoDaConversa = 1.70;

    /// <summary>
    /// A chance por sorteio com tudo neutro: ~1,3 mensagem num dia de 14 h (84 sorteios). Era
    /// 2,5% quando o modelo ainda podia responder NADA; agora todo acerto é mensagem.
    /// </summary>
    public const double ChanceBase = 0.015;

    public static readonly TimeSpan Cadencia = TimeSpan.FromMinutes(10);

    public const double Minimo = 0.2;
    public const double Maximo = 3.0;

    /// <summary>
    /// Teto padrão de mensagens por iniciativa num dia: é o que limita o custo. O usuário
    /// escolhe o dele (<see cref="UserAppSettings.MensagensPorDia"/>).
    /// </summary>
    public const int PadraoDeMensagensPorDia = 6;

    /// <summary>Quantas mensagens e ganchos recentes ela lembra, para não repetir.</summary>
    public const int Memoria = 5;

    /// <summary>Sem resposta depois disso, a fala é classificada como não respondida.</summary>
    public static readonly TimeSpan Paciencia = TimeSpan.FromHours(8);

    /// <summary>Depois da primeira resposta, quanto tempo a conversa ainda conta para a fala.</summary>
    public static readonly TimeSpan JanelaDaConversa = TimeSpan.FromMinutes(30);

    /// <summary>Pausa depois de "agora não" (ou até o fim do dia, o que vier primeiro).</summary>
    public static readonly TimeSpan PausaDaRecusa = TimeSpan.FromHours(4);

    /// <summary>Quanto os multiplicadores voltam para 1 por dia (no logaritmo).</summary>
    public const double Esquecimento = 0.10;

    /// <summary>Geral a partir do qual a ponderação diz que eles têm conversado bastante.</summary>
    public const double Proximidade = 1.5;

    /// <summary>Usuário sem mexer no PC há mais que isso não está lá para ler.</summary>
    public static readonly TimeSpan Ausencia = TimeSpan.FromMinutes(5);

    /// <summary>Conversa mais recente que isso: ele está conversando, não é hora de puxar assunto.</summary>
    public static readonly TimeSpan Calma = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly string _raiz;
    private readonly string _arquivo;
    private readonly string _legado;
    private readonly object _gate = new();
    private Temperamento _temperamento = Temperamento.Padrao;

    /// <param name="raiz">Pasta alternativa. Existe para o teste não escrever no ~/.AIB real.</param>
    /// <param name="personagem">
    /// O personagem ativo. Vazio: um arquivo só, com tudo, como era antes de o vínculo ser por
    /// personagem.
    /// </param>
    public Iniciativa(string? raiz = null, string? personagem = null, Temperamento? temperamento = null)
    {
        _raiz = raiz ?? DirectoryService.DataDir;
        _arquivo = Path.Combine(_raiz, "iniciativa.dat");
        _legado = Path.Combine(_raiz, "iniciativa.json");
        Estado = Ler();
        _temperamento = temperamento ?? Temperamento.Padrao;

        // No arranque, personagem sem vínculo gravado fica com o que o arquivo geral trazia: é a
        // migração do arquivo único, em que tudo o que se aprendeu era do personagem ativo.
        Personagem = Seguro(personagem);
        if (Personagem.Length > 0)
        {
            LerVinculo()?.Para(Estado);
            Gravar();
        }
    }

    public EstadoDaIniciativa Estado { get; private set; }

    /// <summary>O personagem cujo vínculo está carregado. Vazio: nenhum.</summary>
    public string Personagem { get; private set; }

    private static string Seguro(string? nome)
    {
        string n = Path.GetFileName((nome ?? "").Trim());
        return n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "" : n;
    }

    private string ArquivoDoVinculo => Path.Combine(_raiz, "character", Personagem, "vinculo.dat");

    /// <summary>
    /// Passa a valer o vínculo de outro personagem: grava o do que sai e carrega o do que entra
    /// (ou começa do zero, se ele nunca conversou). O que é do usuário — faixas, pausa, contagem
    /// do dia — continua.
    /// </summary>
    public void Trocar(string? personagem, Temperamento? temperamento = null)
    {
        lock (_gate)
        {
            _temperamento = temperamento ?? Temperamento.Padrao;

            string novo = Seguro(personagem);
            if (novo == Personagem) return;

            Gravar();
            Personagem = novo;
            (LerVinculo() ?? new Vinculo()).Para(Estado);
            Gravar();
        }
    }

    // ── Decisões puras ────────────────────────────────────────────────────

    /// <summary>"HH:mm" lido, ou null.</summary>
    public static TimeSpan? Hora(string? texto) =>
        TimeSpan.TryParseExact((texto ?? "").Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t)
        && t < TimeSpan.FromDays(1) ? t : null;

    /// <summary>Se a hora cai no silêncio. O silêncio pode atravessar a meia-noite (22h–8h).</summary>
    public static bool EmSilencio(TimeSpan hora, TimeSpan inicio, TimeSpan fim)
    {
        if (inicio == fim) return false;
        return inicio < fim ? hora >= inicio && hora < fim : hora >= inicio || hora < fim;
    }

    public static int FaixaDe(TimeSpan hora) => Math.Clamp((int)(hora.TotalHours / 2), 0, NumeroDeFaixas - 1);

    /// <summary>A chance deste sorteio. <paramref name="temperamento"/> é a do personagem (1 é o padrão).</summary>
    public static double Chance(EstadoDaIniciativa e, TimeSpan hora, double temperamento = 1) =>
        ChanceBase * temperamento * e.Geral * e.Faixas[FaixaDe(hora)];

    /// <summary>O que impede de sortear agora, ou null quando pode.</summary>
    public static string? Impedimento(
        EstadoDaIniciativa e, DateTime agoraUtc, TimeSpan horaLocal, TimeSpan silencioInicio, TimeSpan silencioFim,
        bool presente, bool livre, DateTime? ultimaConversaUtc, int tetoDoDia = PadraoDeMensagensPorDia)
    {
        if (EmSilencio(horaLocal, silencioInicio, silencioFim)) return "silêncio";
        if (e.PausaAteUtc is DateTime p && agoraUtc < p) return "pausa pedida";
        if (!presente) return "ausente ou ocupado";
        if (!livre) return "turno ou conversa aberta";
        if (ultimaConversaUtc is DateTime c && agoraUtc - c < Calma) return "conversa recente";
        if (e.FalaUtc != null && e.RespostaUtc == null) return "esperando resposta";
        if (e.MensagensHoje >= tetoDoDia) return "teto de mensagens";
        if (e.SorteioUtc is DateTime s && agoraUtc - s < Cadencia) return "sorteou há pouco";
        return null;
    }

    /// <summary>
    /// O fator de uma fala já encerrada. Conversa: 1,10 com 2 turnos, +0,05 por turno a mais,
    /// até 1,70 — decisão do usuário, pela alma afetuosa dela.
    /// </summary>
    /// <param name="teto">O teto da conversa: o apego do personagem (<see cref="Temperamento.Apego"/>).</param>
    public static double Fator(bool recusou, bool respondeu, int turnos, int palavras, bool leu,
                               double teto = TetoDaConversa)
    {
        if (recusou) return 0.5;
        if (!respondeu) return leu ? 0.9 : 0.75;
        if (turnos >= 2) return Math.Min(teto, 1.10 + 0.05 * (turnos - 2));
        return palavras >= 12 ? 1.10 : 1.05;
    }

    /// <summary>
    /// O fator de uma conversa que ele puxou no orbe: 1,02 com uma mensagem só, 1,05 com duas,
    /// +0,02 por turno a mais, até 1,20. Nunca desce, e não olha "agora não": numa mensagem que
    /// não responde a ela, "para de rodar o build" é trabalho, não recusa.
    /// </summary>
    /// <param name="apego">O apego do personagem: a conversa puxada não sobe mais que ele.</param>
    public static double FatorEspontaneo(int turnos, double apego = TetoDaConversa) =>
        turnos >= 2 ? Math.Min(Math.Min(1.20, apego), 1.05 + 0.02 * (turnos - 2)) : 1.02;

    private static readonly Regex Recusa = new(
        @"\b(agora n[aã]o|depois a gente|depois falamos|para de|pare de|chega|n[aã]o quero conversar|me deixa|fala menos|menos mensage)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Se a resposta é um "agora não".</summary>
    public static bool EhRecusa(string? texto) => Recusa.IsMatch(texto ?? "");

    /// <summary>
    /// Se o texto do modelo não serve como mensagem. Não há mais saída NADA no pedido, mas um
    /// modelo pode devolver vazio ou insistir nela; aí não se manda nada.
    /// </summary>
    public static bool EhSilencio(string? texto)
    {
        string t = (texto ?? "").Trim().Trim('.', '!', '"', '*', ' ').Trim();
        return t.Length == 0 || t.Equals("NADA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// O gancho da mensagem: uma pendência de assunto ou um fato sobre o usuário, COM O MESMO
    /// PESO — decisão do usuário: ela não está ajudando no trabalho naquela hora, e quando a
    /// mensagem chega ele talvez nem esteja lidando com aquilo, então a pendência não vale mais
    /// que o resto. Os usados há pouco ficam de fora enquanto houver outro. Nulo quando não há
    /// nenhum: aí o pedido é para conhecê-lo melhor.
    /// </summary>
    public static Gancho? EscolherGancho(
        System.Collections.Generic.IReadOnlyList<string> pendencias,
        System.Collections.Generic.IReadOnlyList<string> fatos,
        System.Collections.Generic.IReadOnlyCollection<string> recentes,
        Random sorte)
    {
        var todos = pendencias.Select(p => new Gancho("pendência", p.Trim()))
            .Concat(fatos.Select(f => new Gancho("fato", f.Trim())))
            .Where(g => g.Texto.Length > 0)
            .ToList();
        if (todos.Count == 0) return null;

        var novos = todos.Where(g => !recentes.Contains(g.Texto)).ToList();
        var escolha = novos.Count > 0 ? novos : todos;
        return escolha[sorte.Next(escolha.Count)];
    }

    /// <summary>O fator em palavras, para ela saber como foi a última vez.</summary>
    public static string Desfecho(double fator) => fator switch
    {
        <= 0.5 => "recebeu um \"agora não\"",
        < 0.8 => "ficou sem resposta",
        < 1.0 => "foi lida, mas ficou sem resposta",
        < 1.08 => "teve uma resposta rápida",
        _ => "virou conversa"
    };

    private static double Preso(double m) => Math.Clamp(m, Minimo, Maximo);

    /// <summary>Um multiplicador andando <see cref="Esquecimento"/> na direção de 1.</summary>
    public static double Esquecer(double m) => Preso(Math.Exp(Math.Log(m) * (1 - Esquecimento)));

    // ── Transições ────────────────────────────────────────────────────────

    /// <summary>Vira o dia: zera a contagem e esquece um pouco.</summary>
    public void NovoDia(DateTime agoraLocal)
    {
        lock (_gate)
        {
            string hoje = agoraLocal.ToString("yyyy-MM-dd");
            if (Estado.Dia == hoje) return;

            bool primeiraVez = Estado.Dia.Length == 0;
            Estado.Dia = hoje;
            Estado.MensagensHoje = 0;
            if (!primeiraVez)
            {
                Estado.Geral = Esquecer(Estado.Geral);
                for (int i = 0; i < Estado.Faixas.Length; i++) Estado.Faixas[i] = Esquecer(Estado.Faixas[i]);
            }
            Gravar();
        }
    }

    /// <summary>Marca o sorteio desta janela de 10 min e diz se acertou.</summary>
    public bool Sortear(DateTime agoraUtc, TimeSpan horaLocal, double dado)
    {
        lock (_gate)
        {
            Estado.SorteioUtc = agoraUtc;
            Gravar();
            return dado < Chance(Estado, horaLocal, _temperamento.Chance);
        }
    }

    /// <summary>
    /// Registra uma mensagem pedida ao modelo. Conta no teto mesmo sem texto (a requisição foi
    /// paga); com texto, abre a fala que vai ser classificada e guarda para não repetir.
    /// </summary>
    public void Falou(DateTime agoraUtc, TimeSpan horaLocal, string? texto, Gancho? gancho)
    {
        lock (_gate)
        {
            Estado.MensagensHoje++;
            if (gancho != null) Lembrar(Estado.GanchosRecentes, gancho.Texto);

            if (!string.IsNullOrWhiteSpace(texto))
            {
                Lembrar(Estado.Recentes, texto.Trim());
                Estado.FalaUtc = agoraUtc;
                Estado.FaixaDaFala = FaixaDe(horaLocal);
                Estado.Leu = false;
                Estado.RespostaUtc = null;
                Estado.Turnos = 0;
                Estado.Palavras = 0;
            }
            Gravar();
        }
    }

    private static void Lembrar(System.Collections.Generic.List<string> lista, string item)
    {
        lista.Remove(item);
        lista.Add(item);
        while (lista.Count > Memoria) lista.RemoveAt(0);
    }

    /// <summary>Ele abriu o pulso.</summary>
    public void Leu()
    {
        lock (_gate)
        {
            if (Estado.FalaUtc == null || Estado.Leu) return;
            Estado.Leu = true;
            Gravar();
        }
    }

    /// <summary>
    /// Ele falou na conversa do orbe. A primeira resposta depois da fala: se for "agora não",
    /// classifica na hora e pausa; senão abre a janela de 30 min em que os turnos contam. Sem
    /// fala dela esperando, é ele puxando conversa: abre (ou soma a) uma conversa espontânea.
    /// </summary>
    /// <param name="horaLocal">A hora local da mensagem. Existe para o teste não depender do fuso.</param>
    public void UsuarioFalou(DateTime agoraUtc, string texto, TimeSpan? horaLocal = null)
    {
        lock (_gate)
        {
            if (Estado.FalaUtc == null)
            {
                if (Estado.ConversaUtc is DateTime c && agoraUtc - c > JanelaDaConversa) FecharConversa();
                if (Estado.ConversaUtc == null)
                {
                    Estado.ConversaUtc = agoraUtc;
                    Estado.FaixaDaConversa = FaixaDe(horaLocal ?? agoraUtc.ToLocalTime().TimeOfDay);
                    Estado.TurnosDaConversa = 0;
                }
                Estado.TurnosDaConversa++;
                Gravar();
                return;
            }

            if (Estado.RespostaUtc == null)
            {
                Estado.RespostaUtc = agoraUtc;
                Estado.Palavras = ConversaDoOrbe.Palavras(texto);

                if (EhRecusa(texto))
                {
                    Aplicar(Fator(recusou: true, true, 1, Estado.Palavras, Estado.Leu));
                    var fimDoDia = agoraUtc.ToLocalTime().Date.AddDays(1).ToUniversalTime();
                    Estado.PausaAteUtc = agoraUtc + PausaDaRecusa < fimDoDia ? agoraUtc + PausaDaRecusa : fimDoDia;
                    Encerrar();
                    Gravar();
                    return;
                }
            }

            if (agoraUtc - Estado.RespostaUtc.Value <= JanelaDaConversa) Estado.Turnos++;
            Gravar();
        }
    }

    /// <summary>
    /// Fecha a fala que já pode ser julgada: 30 min depois da primeira resposta, ou 8 h sem
    /// resposta. Fecha também a conversa espontânea, 30 min depois de ele a ter começado.
    /// Chamado a cada batida.
    /// </summary>
    public void Classificar(DateTime agoraUtc)
    {
        lock (_gate)
        {
            if (Estado.ConversaUtc is DateTime c && agoraUtc - c >= JanelaDaConversa)
            {
                FecharConversa();
                Gravar();
            }

            if (Estado.FalaUtc is not DateTime fala) return;

            if (Estado.RespostaUtc is DateTime r)
            {
                if (agoraUtc - r < JanelaDaConversa) return;
                Aplicar(Fator(false, respondeu: true, Estado.Turnos, Estado.Palavras, Estado.Leu, _temperamento.Apego));
            }
            else
            {
                if (agoraUtc - fala < Paciencia) return;
                Aplicar(Fator(false, respondeu: false, 0, 0, Estado.Leu));
            }

            Encerrar();
            Gravar();
        }
    }

    /// <summary>O fator inteiro na faixa da fala, e a raiz dele no geral.</summary>
    private void Aplicar(double fator)
    {
        Estado.UltimoDesfecho = Desfecho(fator);
        Multiplicar(Estado.FaixaDaFala, fator);
    }

    private void Multiplicar(int faixa, double fator)
    {
        int f = Math.Clamp(faixa, 0, NumeroDeFaixas - 1);
        Estado.Faixas[f] = Preso(Estado.Faixas[f] * fator);
        Estado.Geral = Preso(Estado.Geral * Math.Sqrt(fator));
    }

    /// <summary>
    /// Fecha a conversa espontânea. Não mexe no desfecho: ele é de como terminou a última
    /// iniciativa DELA, e é o que ela lê no próximo pedido.
    /// </summary>
    private void FecharConversa()
    {
        if (Estado.TurnosDaConversa > 0)
            Multiplicar(Estado.FaixaDaConversa, FatorEspontaneo(Estado.TurnosDaConversa, _temperamento.Apego));
        Estado.ConversaUtc = null;
        Estado.TurnosDaConversa = 0;
    }

    private void Encerrar()
    {
        Estado.FalaUtc = null;
        Estado.RespostaUtc = null;
        Estado.Leu = false;
        Estado.Turnos = 0;
        Estado.Palavras = 0;
    }

    /// <summary>
    /// O aprendizado numa frase, para a página Shadow: "Geral 1,20 · mais à vontade das 8h às
    /// 10h · menos das 14h às 16h".
    /// </summary>
    public static string Resumo(EstadoDaIniciativa e)
    {
        var br = new CultureInfo("pt-BR");
        var partes = new System.Collections.Generic.List<string> { "Geral " + e.Geral.ToString("0.00", br) };

        int melhor = Array.IndexOf(e.Faixas, e.Faixas.Max());
        int pior = Array.IndexOf(e.Faixas, e.Faixas.Min());
        string Faixa(int i) => $"das {i * 2}h às {i * 2 + 2}h";

        if (e.Faixas[melhor] >= 1.05) partes.Add("mais à vontade " + Faixa(melhor));
        if (e.Faixas[pior] <= 0.95) partes.Add("menos " + Faixa(pior));
        if (partes.Count == 1) partes.Add("ainda sem preferência de horário");

        return string.Join(" · ", partes);
    }

    /// <summary>Zera o aprendizado (botão das configurações).</summary>
    public void Zerar()
    {
        lock (_gate)
        {
            Estado = new EstadoDaIniciativa { Dia = Estado.Dia, MensagensHoje = Estado.MensagensHoje };
            Gravar();
        }
    }

    /// <summary>O arquivo geral, ou o <c>iniciativa.json</c> em texto claro de antes da cifra.</summary>
    private EstadoDaIniciativa Ler()
    {
        try
        {
            string? json = ArquivoCifrado.Ler(File.Exists(_arquivo) ? _arquivo : _legado);
            if (json != null)
            {
                var e = JsonSerializer.Deserialize<EstadoDaIniciativa>(json, Json);
                if (e != null && e.Faixas?.Length == NumeroDeFaixas) return e;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INICIATIVA] Falha ao ler {_arquivo}: {ex.Message}");
        }
        return new EstadoDaIniciativa();
    }

    private Vinculo? LerVinculo()
    {
        try
        {
            string? json = ArquivoCifrado.Ler(ArquivoDoVinculo);
            return json == null ? null : JsonSerializer.Deserialize<Vinculo>(json, Json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INICIATIVA] Falha ao ler {ArquivoDoVinculo}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Com personagem, grava em dois: o vínculo na pasta dele, e no geral só o que é do usuário
    /// (o resto vai em branco, para outro personagem não herdar). Sem personagem, tudo no geral.
    /// </summary>
    private void Gravar()
    {
        try
        {
            var geral = Estado;
            if (Personagem.Length > 0)
            {
                ArquivoCifrado.Gravar(ArquivoDoVinculo, JsonSerializer.Serialize(Vinculo.De(Estado), Json));
                geral = new EstadoDaIniciativa
                {
                    Faixas = Estado.Faixas,
                    PausaAteUtc = Estado.PausaAteUtc,
                    SorteioUtc = Estado.SorteioUtc,
                    Dia = Estado.Dia,
                    MensagensHoje = Estado.MensagensHoje
                };
            }

            ArquivoCifrado.Gravar(_arquivo, JsonSerializer.Serialize(geral, Json));

            // O texto claro de antes da cifra já foi lido e regravado cifrado: não fica para trás.
            if (File.Exists(_legado)) File.Delete(_legado);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INICIATIVA] Falha ao gravar {_arquivo}: {ex.Message}");
        }
    }
}

/// <summary>Se o usuário está no PC e disponível, pelo que o Windows sabe.</summary>
public static class Presenca
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    /// <summary>QUNS_ACCEPTS_NOTIFICATIONS: nada de tela cheia, apresentação ou "não perturbe".</summary>
    private const int AceitaAvisos = 5;

    /// <summary>Há quanto tempo o usuário não mexe em teclado nem mouse.</summary>
    public static TimeSpan Ocioso()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Se o Windows aceita avisos agora. Na dúvida (a chamada falhou), não.</summary>
    public static bool AceitaAviso() =>
        SHQueryUserNotificationState(out int estado) == 0 && estado == AceitaAvisos;

    /// <summary>No PC, mexendo, e disponível.</summary>
    public static bool Disponivel() => Ocioso() < Iniciativa.Ausencia && AceitaAviso();
}
