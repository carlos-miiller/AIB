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
/// O que a iniciativa aprendeu, gravado em <c>~/.AIB/iniciativa.json</c> para sobreviver ao
/// arranque.
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
/// Travas que não aprendem: com uma fala sem resposta ela não sorteia; teto de 6 mensagens por
/// dia (cada uma é uma requisição paga); silêncio, presença e "não perturbe".
/// </para>
/// </summary>
public sealed class Iniciativa
{
    public const int NumeroDeFaixas = 12;

    /// <summary>
    /// A chance por sorteio com tudo neutro: ~1,3 mensagem num dia de 14 h (84 sorteios). Era
    /// 2,5% quando o modelo ainda podia responder NADA; agora todo acerto é mensagem.
    /// </summary>
    public const double ChanceBase = 0.015;

    public static readonly TimeSpan Cadencia = TimeSpan.FromMinutes(10);

    public const double Minimo = 0.2;
    public const double Maximo = 3.0;

    /// <summary>Teto de mensagens por iniciativa num dia: é o que limita o custo.</summary>
    public const int MensagensPorDia = 6;

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

    private readonly string _arquivo;
    private readonly object _gate = new();

    /// <param name="raiz">Pasta alternativa. Existe para o teste não escrever no ~/.AIB real.</param>
    public Iniciativa(string? raiz = null)
    {
        _arquivo = Path.Combine(raiz ?? DirectoryService.DataDir, "iniciativa.json");
        Estado = Ler();
    }

    public EstadoDaIniciativa Estado { get; private set; }

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

    /// <summary>A chance deste sorteio.</summary>
    public static double Chance(EstadoDaIniciativa e, TimeSpan hora) =>
        ChanceBase * e.Geral * e.Faixas[FaixaDe(hora)];

    /// <summary>O que impede de sortear agora, ou null quando pode.</summary>
    public static string? Impedimento(
        EstadoDaIniciativa e, DateTime agoraUtc, TimeSpan horaLocal, TimeSpan silencioInicio, TimeSpan silencioFim,
        bool presente, bool livre, DateTime? ultimaConversaUtc)
    {
        if (EmSilencio(horaLocal, silencioInicio, silencioFim)) return "silêncio";
        if (e.PausaAteUtc is DateTime p && agoraUtc < p) return "pausa pedida";
        if (!presente) return "ausente ou ocupado";
        if (!livre) return "turno ou conversa aberta";
        if (ultimaConversaUtc is DateTime c && agoraUtc - c < Calma) return "conversa recente";
        if (e.FalaUtc != null && e.RespostaUtc == null) return "esperando resposta";
        if (e.MensagensHoje >= MensagensPorDia) return "teto de mensagens";
        if (e.SorteioUtc is DateTime s && agoraUtc - s < Cadencia) return "sorteou há pouco";
        return null;
    }

    /// <summary>
    /// O fator de uma fala já encerrada. Conversa: 1,10 com 2 turnos, +0,05 por turno a mais,
    /// até 1,70 — decisão do usuário, pela alma afetuosa dela.
    /// </summary>
    public static double Fator(bool recusou, bool respondeu, int turnos, int palavras, bool leu)
    {
        if (recusou) return 0.5;
        if (!respondeu) return leu ? 0.9 : 0.75;
        if (turnos >= 2) return Math.Min(1.70, 1.10 + 0.05 * (turnos - 2));
        return palavras >= 12 ? 1.10 : 1.05;
    }

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
            return dado < Chance(Estado, horaLocal);
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
    /// classifica na hora e pausa; senão abre a janela de 30 min em que os turnos contam.
    /// </summary>
    public void UsuarioFalou(DateTime agoraUtc, string texto)
    {
        lock (_gate)
        {
            if (Estado.FalaUtc == null) return;

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
    /// resposta. Chamado a cada batida.
    /// </summary>
    public void Classificar(DateTime agoraUtc)
    {
        lock (_gate)
        {
            if (Estado.FalaUtc is not DateTime fala) return;

            if (Estado.RespostaUtc is DateTime r)
            {
                if (agoraUtc - r < JanelaDaConversa) return;
                Aplicar(Fator(false, respondeu: true, Estado.Turnos, Estado.Palavras, Estado.Leu));
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
        int f = Math.Clamp(Estado.FaixaDaFala, 0, NumeroDeFaixas - 1);
        Estado.Faixas[f] = Preso(Estado.Faixas[f] * fator);
        Estado.Geral = Preso(Estado.Geral * Math.Sqrt(fator));
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

    private EstadoDaIniciativa Ler()
    {
        try
        {
            if (File.Exists(_arquivo))
            {
                var e = JsonSerializer.Deserialize<EstadoDaIniciativa>(File.ReadAllText(_arquivo, Encoding.UTF8), Json);
                if (e != null && e.Faixas?.Length == NumeroDeFaixas) return e;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INICIATIVA] Falha ao ler {_arquivo}: {ex.Message}");
        }
        return new EstadoDaIniciativa();
    }

    private void Gravar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
            File.WriteAllText(_arquivo, JsonSerializer.Serialize(Estado, Json), new UTF8Encoding(false));
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
