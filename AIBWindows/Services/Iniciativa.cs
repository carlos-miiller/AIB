using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services;

/// <summary>
/// O estado do ritmo, gravado em <c>~/.AIB/iniciativa.json</c> para sobreviver ao arranque.
/// </summary>
public sealed class EstadoDaIniciativa
{
    /// <summary>Quantas iniciativas por dia o ritmo pede agora.</summary>
    public double Ritmo { get; set; } = Iniciativa.RitmoInicial;

    /// <summary>Quando ela falou por iniciativa pela última vez.</summary>
    public DateTime? UltimaFalaUtc { get; set; }

    /// <summary>Se a última fala ainda espera resposta. Enquanto espera, ela não insiste.</summary>
    public bool Aguardando { get; set; }

    /// <summary>Quando ela ponderou pela última vez, falando ou não.</summary>
    public DateTime? UltimaPonderacaoUtc { get; set; }

    /// <summary>O dia local das contagens, "yyyy-MM-dd".</summary>
    public string Dia { get; set; } = "";

    public int FalasHoje { get; set; }
    public int PonderacoesHoje { get; set; }
}

/// <summary>
/// A persona puxa assunto sozinha: retoma algo que ficou em aberto, pergunta sobre o usuário.
/// <para>
/// O RITMO É ADAPTATIVO, por decisão do usuário: começa devagar, sobe quando ele responde e cai
/// quando ele ignora. Sem intervalo mínimo fixo — o espaço entre uma fala e outra sai do ritmo
/// (horas acordadas ÷ ritmo) —, e com uma regra que segura quase todo excesso: enquanto a
/// última fala não foi respondida, ela não manda outra.
/// </para>
/// <para>
/// Cada ponderação é uma requisição paga, mesmo quando ela decide ficar quieta (NADA). Por isso
/// há teto de falas e de ponderações por dia, e ela só pondera com o usuário no computador,
/// fora de tela cheia e "não perturbe", fora do horário de silêncio, sem turno rodando e sem
/// conversa nos últimos minutos.
/// </para>
/// </summary>
public sealed class Iniciativa
{
    public const double RitmoInicial = 2;
    public const double RitmoMinimo = 0.5;

    /// <summary>Teto de falas por dia: o ritmo nunca passa disso, e é o que limita o custo.</summary>
    public const double RitmoMaximo = 6;

    /// <summary>Teto de ponderações por dia, contando as que terminam em NADA.</summary>
    public const int PonderacoesPorDia = 12;

    /// <summary>Sem resposta depois disso, a fala conta como ignorada e o ritmo cai.</summary>
    public static readonly TimeSpan Paciencia = TimeSpan.FromHours(8);

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

    /// <summary>Horas fora do silêncio num dia.</summary>
    public static TimeSpan HorasAcordadas(TimeSpan inicio, TimeSpan fim)
    {
        if (inicio == fim) return TimeSpan.FromHours(24);
        var silencio = inicio < fim ? fim - inicio : TimeSpan.FromHours(24) - inicio + fim;
        return TimeSpan.FromHours(24) - silencio;
    }

    /// <summary>
    /// O espaço entre duas falas que o ritmo pede: as horas acordadas divididas pelas falas do
    /// dia. Ritmo 2 num dia de 14 h: uma fala a cada 7 h. Ritmo 6: a cada 2h20.
    /// </summary>
    public static TimeSpan Espaco(double ritmo, TimeSpan acordadas) =>
        TimeSpan.FromTicks((long)(acordadas.Ticks / Math.Clamp(ritmo, RitmoMinimo, RitmoMaximo)));

    /// <summary>O que impede de ponderar agora, ou null quando pode.</summary>
    public static string? Impedimento(
        EstadoDaIniciativa e, DateTime agoraUtc, TimeSpan horaLocal, TimeSpan silencioInicio, TimeSpan silencioFim,
        bool presente, bool livre, DateTime? ultimaConversaUtc)
    {
        if (EmSilencio(horaLocal, silencioInicio, silencioFim)) return "silêncio";
        if (!presente) return "ausente ou ocupado";
        if (!livre) return "turno ou conversa aberta";
        if (ultimaConversaUtc is DateTime c && agoraUtc - c < Calma) return "conversa recente";
        if (e.Aguardando) return "esperando resposta";
        if (e.FalasHoje >= (int)Math.Floor(e.Ritmo + 0.5) || e.FalasHoje >= RitmoMaximo) return "ritmo do dia cumprido";
        if (e.PonderacoesHoje >= PonderacoesPorDia) return "teto de ponderações";

        var espaco = Espaco(e.Ritmo, HorasAcordadas(silencioInicio, silencioFim));
        if (e.UltimaFalaUtc is DateTime f && agoraUtc - f < espaco) return "cedo para outra fala";

        // Ficou quieta da última vez: pondera de novo só depois de meio espaço, senão cada
        // batida viraria uma requisição terminando em NADA.
        if (e.UltimaPonderacaoUtc is DateTime p && agoraUtc - p < espaco / 2) return "ponderou há pouco";

        return null;
    }

    /// <summary>Se o texto do modelo é a escolha de ficar quieta.</summary>
    public static bool EhSilencio(string? texto)
    {
        string t = (texto ?? "").Trim().Trim('.', '!', '"', '*', ' ').Trim();
        return t.Length == 0 || t.Equals("NADA", StringComparison.OrdinalIgnoreCase);
    }

    // ── Transições ────────────────────────────────────────────────────────

    /// <summary>Vira o dia: zera as contagens.</summary>
    public void NovoDia(DateTime agoraLocal)
    {
        lock (_gate)
        {
            string hoje = agoraLocal.ToString("yyyy-MM-dd");
            if (Estado.Dia == hoje) return;
            Estado.Dia = hoje;
            Estado.FalasHoje = 0;
            Estado.PonderacoesHoje = 0;
            Gravar();
        }
    }

    /// <summary>Fala sem resposta além da paciência: conta como ignorada, e o ritmo cai.</summary>
    public void ConferirPaciencia(DateTime agoraUtc)
    {
        lock (_gate)
        {
            if (!Estado.Aguardando || Estado.UltimaFalaUtc is not DateTime f || agoraUtc - f < Paciencia) return;
            Estado.Aguardando = false;
            Estado.Ritmo = Math.Max(RitmoMinimo, Estado.Ritmo * 0.6);
            Gravar();
        }
    }

    /// <summary>O usuário falou depois de uma iniciativa: ela fica um pouco mais presente.</summary>
    public void Respondeu()
    {
        lock (_gate)
        {
            if (!Estado.Aguardando) return;
            Estado.Aguardando = false;
            Estado.Ritmo = Math.Min(RitmoMaximo, Estado.Ritmo + 0.5);
            Gravar();
        }
    }

    /// <summary>Registra uma ponderação, e se ela terminou em fala.</summary>
    public void Ponderou(DateTime agoraUtc, bool falou)
    {
        lock (_gate)
        {
            Estado.UltimaPonderacaoUtc = agoraUtc;
            Estado.PonderacoesHoje++;
            if (falou)
            {
                Estado.UltimaFalaUtc = agoraUtc;
                Estado.FalasHoje++;
                Estado.Aguardando = true;
            }
            Gravar();
        }
    }

    private EstadoDaIniciativa Ler()
    {
        try
        {
            if (File.Exists(_arquivo))
                return JsonSerializer.Deserialize<EstadoDaIniciativa>(File.ReadAllText(_arquivo, Encoding.UTF8), Json)
                       ?? new EstadoDaIniciativa();
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
