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

    /// <summary>
    /// A última vez que os dois se falaram, no orbe ou na janela, ou que ela falou. É de onde a
    /// saudade conta (<see cref="Iniciativa.Saudade"/>). Nula: ainda não se sabe.
    /// </summary>
    public DateTime? ContatoUtc { get; set; }

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
    public DateTime? ContatoUtc { get; set; }
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
        ContatoUtc = e.ContatoUtc,
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
        e.ContatoUtc = ContatoUtc;
        e.Recentes = (Recentes ?? new()).ToList();
        e.GanchosRecentes = (GanchosRecentes ?? new()).ToList();
        e.UltimoDesfecho = UltimoDesfecho ?? "";
    }
}

/// <summary>
/// O temperamento de um personagem na iniciativa: os <see cref="Atributos"/> dele (do arquivo
/// de status) já traduzidos nos números da conta. O padrão é o de quando não há personagem: o
/// que valia para todos antes dos atributos.
/// </summary>
/// <param name="Chance">Multiplica a chance base do sorteio. 1 é o padrão; menos, mais calado.</param>
/// <param name="Apego">
/// O teto do fator de conversa. 1,70 nasceu da alma afetuosa da Ellen; um personagem mais seco
/// sobe menos com a mesma conversa.
/// </param>
public readonly record struct Temperamento(double Chance, double Apego)
{
    /// <summary>O fator de uma fala que ficou sem resposta e sem ser lida.</summary>
    public double Ignorada { get; init; } = Iniciativa.FatorIgnorada;

    /// <summary>O fator de um "agora não".</summary>
    public double Recusada { get; init; } = Iniciativa.FatorRecusada;

    /// <summary>Quanto o geral dele volta para 1 por dia.</summary>
    public double Esquecimento { get; init; } = Iniciativa.Esquecimento;

    /// <summary>
    /// O quanto a saudade dele cresce com o tempo sem contato (<see cref="Iniciativa.Saudade"/>):
    /// 1 no afeto neutro, um pouco mais com afeto alto. O padrão é o do afeto +2.
    /// </summary>
    public double Saudade { get; init; } = SaudadeDoAfeto(2);

    /// <summary>A chance de, tendo assunto para retomar, ele preferir perguntar sobre o usuário.</summary>
    public double Curiosidade { get; init; } = CuriosidadePorNivel[Atributos.Neutro - 1];

    // O que cada nível vale, do 1 ao 5. O do meio é o comportamento de antes dos atributos —
    // menos na curiosidade, que não existia: antes ele só perguntava sem ter assunto nenhum.
    // O afeto não tem tabela: é uma conta (TetoDoAfeto).
    private static readonly double[] ChancePorNivel = { 0.5, 0.75, 1, 1.3, 1.6 };
    private static readonly double[] IgnoradaPorNivel = { 0.60, 0.68, Iniciativa.FatorIgnorada, 0.83, 0.90 };
    private static readonly double[] RecusadaPorNivel = { 0.35, 0.42, Iniciativa.FatorRecusada, 0.60, 0.70 };
    private static readonly double[] EsquecimentoPorNivel = { 0.20, 0.15, Iniciativa.Esquecimento, 0.07, 0.05 };
    private static readonly double[] CuriosidadePorNivel = { 0, 0.10, 0.20, 0.35, 0.50 };

    // Depois das escalas: o inicializador da curiosidade lê uma delas.
    public static readonly Temperamento Padrao = new(1, Iniciativa.TetoDaConversa);

    /// <summary>Nível fora de 1–5 (zero de um campo ausente, erro de digitação) vale o neutro ou a ponta.</summary>
    private static double Nivel(double[] escala, int nivel) =>
        escala[Math.Clamp(nivel <= 0 ? Atributos.Neutro : nivel, 1, escala.Length) - 1];

    /// <summary>
    /// O teto da conversa pelo afeto: 1,50 no neutro e 0,10 por ponto. Com -5 dá 1,00 — conversa
    /// longa não o faz puxar mais assunto; com +2, o 1,70 que nasceu com a Ellen; com +5, 2,00.
    /// </summary>
    public static double TetoDoAfeto(double afeto) =>
        Math.Round(1.50 + 0.10 * Math.Clamp(afeto, Atributos.AfetoMinimo, Atributos.AfetoMaximo), 4);

    /// <summary>
    /// O afeto na saudade — pedido do usuário: "quanto maior o afeto, ele sobe levemente mais".
    /// 6% por ponto: 0,70 com -5, 1,12 com +2, 1,30 com +5.
    /// </summary>
    public static double SaudadeDoAfeto(double afeto) =>
        Math.Round(1 + 0.06 * Math.Clamp(afeto, Atributos.AfetoMinimo, Atributos.AfetoMaximo), 4);

    public static Temperamento De(Atributos? a) => a == null
        ? Padrao
        : new(Nivel(ChancePorNivel, a.Iniciativa), TetoDoAfeto(a.Afeto))
        {
            Saudade = SaudadeDoAfeto(a.Afeto),
            Ignorada = Nivel(IgnoradaPorNivel, a.Resiliencia),
            Recusada = Nivel(RecusadaPorNivel, a.Resiliencia),
            Esquecimento = Nivel(EsquecimentoPorNivel, a.Constancia),
            Curiosidade = Nivel(CuriosidadePorNivel, a.Curiosidade)
        };
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
/// SAUDADE: a chance cresce com o tempo sem contato (<see cref="Saudade"/>), até triplicar com
/// um dia. É o que impede o dia inteiro de silêncio que o sorteio puro deixava acontecer.
/// </para>
/// <para>
/// ESSES NÚMEROS SÃO OS DO PERSONAGEM NEUTRO. Cada personagem tem cinco atributos de 1 a 5 no
/// arquivo de status (<see cref="StatusDosPersonagens"/>), e <see cref="Temperamento.De"/> os
/// traduz: chance, teto da conversa, peso de ser ignorada ou recusada, esquecimento, curiosidade.
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

    /// <summary>O fator padrão da fala sem resposta e sem leitura (<see cref="Temperamento.Ignorada"/>).</summary>
    public const double FatorIgnorada = 0.75;

    /// <summary>O fator padrão do "agora não" (<see cref="Temperamento.Recusada"/>).</summary>
    public const double FatorRecusada = 0.5;

    /// <summary>O fator da fala lida e não respondida. Não desce abaixo do da ignorada.</summary>
    public const double FatorLida = 0.9;

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

    /// <summary>O temperamento do personagem carregado.</summary>
    public Temperamento Temperamento { get { lock (_gate) return _temperamento; } }

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

    /// <summary>
    /// A chance deste sorteio. <paramref name="temperamento"/> é a do personagem (1 é o padrão) e
    /// <paramref name="saudade"/>, o quanto o tempo sem contato a aumenta (<see cref="Saudade"/>).
    /// </summary>
    public static double Chance(EstadoDaIniciativa e, TimeSpan hora, double temperamento = 1, double saudade = 1) =>
        ChanceBase * temperamento * saudade * e.Geral * e.Faixas[FaixaDe(hora)];

    /// <summary>Sem contato há menos que isso, ainda não há saudade.</summary>
    public static readonly TimeSpan SaudadeComeca = TimeSpan.FromHours(2);

    /// <summary>Sem contato há isso, a chance dobra (no afeto neutro).</summary>
    public static readonly TimeSpan SaudadeDobra = TimeSpan.FromHours(8);

    /// <summary>Sem contato há isso, a chance triplica, e daí não passa.</summary>
    public static readonly TimeSpan SaudadeTriplica = TimeSpan.FromHours(24);

    /// <summary>
    /// O multiplicador da saudade: 1 até 2 h sem contato, sobe até 2 com 8 h e até 3 com 24 h.
    /// <para>
    /// Visto no uso: um dia inteiro com o orbe na tela e nenhuma mensagem dela. Não era defeito
    /// — a 1,6% por sorteio, um dia de 8 h no PC passa em branco quase metade das vezes. Com a
    /// saudade o dia normal fica igual (ela só conta depois de 2 h) e o silêncio longo fica raro.
    /// </para>
    /// </summary>
    /// <param name="afeto">Multiplica o crescimento (<see cref="Temperamento.Saudade"/>).</param>
    public static double Saudade(TimeSpan semContato, double afeto = 1)
    {
        double ganho;
        if (semContato <= SaudadeComeca) ganho = 0;
        else if (semContato <= SaudadeDobra) ganho = (semContato - SaudadeComeca) / (SaudadeDobra - SaudadeComeca);
        else if (semContato <= SaudadeTriplica) ganho = 1 + (semContato - SaudadeDobra) / (SaudadeTriplica - SaudadeDobra);
        else ganho = 2;

        return 1 + ganho * Math.Max(0, afeto);
    }

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
    /// <param name="ignorada">O fator da fala ignorada: a resiliência do personagem.</param>
    /// <param name="recusada">O fator do "agora não": também a resiliência.</param>
    public static double Fator(bool recusou, bool respondeu, int turnos, int palavras, bool leu,
                               double teto = TetoDaConversa, double ignorada = FatorIgnorada,
                               double recusada = FatorRecusada)
    {
        if (recusou) return recusada;
        if (!respondeu) return leu ? Math.Max(FatorLida, ignorada) : ignorada;
        // O teto vale também para a resposta curta: com afeto -5 ele é 1,00, e nada sobe.
        if (turnos >= 2) return Math.Min(teto, 1.10 + 0.05 * (turnos - 2));
        return Math.Min(teto, palavras >= 12 ? 1.10 : 1.05);
    }

    /// <summary>
    /// O fator de uma conversa que ele puxou no orbe: 1,02 com uma mensagem só, 1,05 com duas,
    /// +0,02 por turno a mais, até 1,20. Nunca desce, e não olha "agora não": numa mensagem que
    /// não responde a ela, "para de rodar o build" é trabalho, não recusa.
    /// </summary>
    /// <param name="apego">O apego do personagem: a conversa puxada não sobe mais que ele.</param>
    public static double FatorEspontaneo(int turnos, double apego = TetoDaConversa) =>
        Math.Min(apego, turnos >= 2 ? Math.Min(1.20, 1.05 + 0.02 * (turnos - 2)) : 1.02);

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
    /// <param name="curiosidade">
    /// A chance de, mesmo havendo gancho, o pedido ser para conhecê-lo melhor
    /// (<see cref="Temperamento.Curiosidade"/>).
    /// </param>
    public static Gancho? EscolherGancho(
        System.Collections.Generic.IReadOnlyList<string> pendencias,
        System.Collections.Generic.IReadOnlyList<string> fatos,
        System.Collections.Generic.IReadOnlyCollection<string> recentes,
        Random sorte,
        double curiosidade = 0)
    {
        var todos = pendencias.Select(p => new Gancho("pendência", p.Trim()))
            .Concat(fatos.Select(f => new Gancho("fato", f.Trim())))
            .Where(g => g.Texto.Length > 0)
            .ToList();
        if (todos.Count == 0) return null;
        if (curiosidade > 0 && sorte.NextDouble() < curiosidade) return null;

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

    /// <summary>
    /// O desfecho pelo que aconteceu. Com a resiliência o fator deixou de dizer sozinho: o
    /// "agora não" de um personagem resiliente vale 0,70, que pelo número seria "sem resposta".
    /// </summary>
    public static string Desfecho(bool recusou, bool respondeu, bool leu, double fator)
    {
        if (recusou) return Desfecho(FatorRecusada);
        if (!respondeu) return Desfecho(leu ? FatorLida : FatorIgnorada);
        return Desfecho(Math.Max(1, fator));
    }

    // ── O afeto anda ──────────────────────────────────────────────────────
    // Devagar: vinte conversas boas para subir os 2 pontos de folga. Recusa pesa mais que
    // conversa, e ser lida sem resposta quase não pesa.
    public const double PassoDaConversa = 0.10;
    public const double PassoDaResposta = 0.03;
    public const double PassoDaEspontanea = 0.05;
    public const double PassoDaLida = -0.02;
    public const double PassoDaIgnorada = -0.05;
    public const double PassoDaRecusa = -0.15;

    /// <summary>Quanto o afeto anda com o desfecho de uma iniciativa.</summary>
    public static double PassoDoAfeto(bool recusou, bool respondeu, bool leu, double fator)
    {
        if (recusou) return PassoDaRecusa;
        if (!respondeu) return leu ? PassoDaLida : PassoDaIgnorada;
        return fator >= 1.08 ? PassoDaConversa : PassoDaResposta;
    }

    /// <summary>
    /// Uma iniciativa ou conversa espontânea foi encerrada, e o afeto do personagem carregado
    /// anda este passo. Quem grava é o arquivo de status (<see cref="StatusDosPersonagens.Mover"/>).
    /// </summary>
    public event Action<double>? AfetoMoveu;

    private static double Preso(double m) => Math.Clamp(m, Minimo, Maximo);

    /// <summary>Um multiplicador andando <paramref name="taxa"/> na direção de 1.</summary>
    public static double Esquecer(double m, double taxa = Esquecimento) =>
        Preso(Math.Exp(Math.Log(m) * (1 - taxa)));

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
                // O geral é do personagem e esquece no ritmo dele; as faixas são do usuário.
                Estado.Geral = Esquecer(Estado.Geral, _temperamento.Esquecimento);
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

            // Sem contato registrado (estado de antes da saudade, ou personagem novo), ela conta
            // a partir de agora.
            Estado.ContatoUtc ??= agoraUtc;
            Gravar();

            double saudade = Saudade(agoraUtc - Estado.ContatoUtc.Value, _temperamento.Saudade);
            return dado < Chance(Estado, horaLocal, _temperamento.Chance, saudade);
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
                Estado.ContatoUtc = agoraUtc;
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
    /// Os dois se falaram na janela. Não conta para o aprendizado — ali ele está trabalhando,
    /// não respondendo a ela —, mas zera a saudade.
    /// </summary>
    public void Contato(DateTime agoraUtc)
    {
        lock (_gate)
        {
            Estado.ContatoUtc = agoraUtc;
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
            Estado.ContatoUtc = agoraUtc;

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
                    Aplicar(_temperamento.Recusada, Desfecho(recusou: true, true, Estado.Leu, 0), PassoDaRecusa);
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
                double fator = Fator(false, respondeu: true, Estado.Turnos, Estado.Palavras, Estado.Leu, _temperamento.Apego);
                Aplicar(fator, Desfecho(false, respondeu: true, Estado.Leu, fator),
                        PassoDoAfeto(false, respondeu: true, Estado.Leu, Fator(false, true, Estado.Turnos, Estado.Palavras, Estado.Leu)));
            }
            else
            {
                if (agoraUtc - fala < Paciencia) return;
                Aplicar(Fator(false, respondeu: false, 0, 0, Estado.Leu, ignorada: _temperamento.Ignorada),
                        Desfecho(false, respondeu: false, Estado.Leu, 0),
                        PassoDoAfeto(false, respondeu: false, Estado.Leu, 0));
            }

            Encerrar();
            Gravar();
        }
    }

    /// <summary>O fator inteiro na faixa da fala, e a raiz dele no geral.</summary>
    private void Aplicar(double fator, string desfecho, double passo)
    {
        Estado.UltimoDesfecho = desfecho;
        Multiplicar(Estado.FaixaDaFala, fator);
        AfetoMoveu?.Invoke(passo);
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

        // Uma mensagem só é pedido de trabalho; a partir de duas, ele quis conversar.
        if (Estado.TurnosDaConversa >= 2) AfetoMoveu?.Invoke(PassoDaEspontanea);
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
