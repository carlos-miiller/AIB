using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIB.Services.Ai;

using Timer = System.Threading.Timer;   // WinForms entra junto com o WPF e traz homonimo.

namespace AIB.Services.Mail;

/// <summary>
/// O vigia: lê as caixas fora da tela, tria e entrega o digest.
/// <para>
/// Até aqui a leitura só acontecia com a tela de configurações aberta, e só contava mensagens.
/// É este serviço que transforma isso em produto — ele roda com a janela fechada, e é a única
/// parte do programa que vê conteúdo de e-mail.
/// </para>
/// <para>
/// REGRA 3, e ela mora aqui: o conteúdo entra pelo <see cref="IMailService.LerAsync"/>, é
/// oferecido ao triador e MORRE no fim do método. Não passa pela conversa, não vai para o
/// <c>raw.jsonl</c>, não vira capítulo. O que sai daqui é <see cref="DigestoDeEmail"/>, que
/// carrega veredito — assunto e remetente para o usuário se achar, nunca corpo.
/// </para>
/// <para>
/// REGRA 2: nada é enviado, arquivado, marcado nem apagado. Toda a leitura é feita com a INBOX
/// em <c>EXAMINE</c>.
/// </para>
/// </summary>
public sealed class MailDigestService : IDisposable
{
    /// <summary>
    /// De quanto em quanto tempo o relógio bate. Não é o intervalo do trabalho: é a resolução
    /// com que se pergunta se chegou a hora. Um minuto é fino o bastante para o digest sair no
    /// horário e grosso o bastante para não custar nada.
    /// </summary>
    public static readonly TimeSpan Batida = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Teto de mensagens num lote de triagem. Acima disso o prefill fica longo demais para uma
    /// máquina pequena, e o digest levaria minutos para começar a sair. O que passa do teto
    /// espera o digest seguinte — as MAIS RECENTES entram primeiro.
    /// </summary>
    public const int TetoDoLote = 25;

    private readonly SettingsService _settings;
    private readonly MailVault _cofre;
    private readonly EstadoDasCaixas _estado;
    private readonly IMailService _email;
    private readonly IChatProviderFactory _provedores;
    private readonly Func<DateTime> _agora;
    private readonly string _caminhoDasRegras;
    private readonly VigiasDoEmail _vigias;

    private readonly CancellationTokenSource _parada = new();
    private Timer? _relogio;
    private int _trabalhando;

    private DateTime? _ultimoDigest;
    private DateTime? _ultimaSondagem;

    public MailDigestService(
        SettingsService settings,
        IMailService email,
        IChatProviderFactory provedores,
        MailVault? cofre = null,
        EstadoDasCaixas? estado = null,
        Func<DateTime>? agora = null,
        string? caminhoDasRegras = null,
        VigiasDoEmail? vigias = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _provedores = provedores ?? throw new ArgumentNullException(nameof(provedores));
        _cofre = cofre ?? new MailVault();
        _estado = estado ?? new EstadoDasCaixas();
        _agora = agora ?? (() => DateTime.Now);
        _caminhoDasRegras = caminhoDasRegras ?? RegrasDoVigia.CaminhoPadrao();
        _vigias = vigias ?? new VigiasDoEmail();
    }

    /// <summary>Um digest ficou pronto e tem algo a dizer.</summary>
    public event Action<DigestoDeEmail>? Pronto;

    /// <summary>Começou a trabalhar — o Shadow acende o anel enquanto isso dura.</summary>
    public event Action? Trabalhando;

    /// <summary>Diagnóstico e ensaio: quantas vezes o laço acordou o modelo.</summary>
    public int DigestosFeitos { get; private set; }

    /// <summary>
    /// O último digest com conteúdo. É o que a aba de e-mails do painel mostra.
    /// <para>
    /// Fica em MEMÓRIA e morre com o programa, de propósito. Gravá-lo seria gravar assunto e
    /// remetente em disco, e a regra 3 do vigia existe justamente para que conteúdo de e-mail
    /// não crie raízes no computador.
    /// </para>
    /// </summary>
    public DigestoDeEmail Ultimo { get; private set; } = DigestoDeEmail.Vazio;

    public void Iniciar()
    {
        if (_relogio != null) return;
        _relogio = new Timer(_ => _ = BaterAsync(), null, Batida, Batida);
        Console.WriteLine($"[VIGIA] Ligado. Digest às {string.Join(", ", AgendaDoVigia.Horarios.Select(h => h.ToString(@"hh\:mm")))}; " +
                          $"sondagem a cada {AgendaDoVigia.IntervaloDaSondagem.TotalMinutes:0} min.");
    }

    public void Parar()
    {
        _relogio?.Dispose();
        _relogio = null;
    }

    /// <summary>Uma batida do relógio. Público para os ensaios não esperarem um minuto.</summary>
    public async Task BaterAsync()
    {
        // Uma passada por vez. Uma leitura demorada não pode acumular batidas atrás dela e
        // disparar três triagens em fila quando a primeira terminar.
        if (Interlocked.Exchange(ref _trabalhando, 1) == 1) return;

        try
        {
            var config = _settings.LoadSettings();
            if (!config.ShadowHandlesMail) return;

            var agora = _agora();

            if (AgendaDoVigia.HoraDoDigest(agora, _ultimoDigest))
            {
                _ultimoDigest = agora;
                _ultimaSondagem = agora;
                await ExecutarAsync(comModelo: true, _parada.Token).ConfigureAwait(false);
                return;
            }

            if (AgendaDoVigia.HoraDaSondagem(agora, _ultimaSondagem))
            {
                _ultimaSondagem = agora;
                await ExecutarAsync(comModelo: false, _parada.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // O vigia não pode derrubar o programa. Ele falha calado no log e tenta de novo na
            // batida seguinte: um erro de rede às 8h25 não pode custar o dia inteiro.
            Console.WriteLine($"[VIGIA] batida falhou — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _trabalhando, 0);
        }
    }

    /// <summary>
    /// Uma passada completa: lê as caixas, passa pelo funil e, se for digest, acorda o modelo.
    /// </summary>
    /// <param name="comModelo">
    /// <c>false</c> é a sondagem de 20 min: só código. Ela existe para achar RAJADA, que é o
    /// único caso em que interromper na hora se justifica — um incidente às 9h14 descoberto no
    /// digest das 12h55 não vale nada.
    /// </param>
    public async Task<DigestoDeEmail> ExecutarAsync(bool comModelo, CancellationToken ct)
    {
        var config = _settings.LoadSettings();
        var caixas = (config.MailAccounts ?? new List<MailAccountSettings>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Address) && _cofre.Existe(c.Address))
            .ToList();

        if (caixas.Count == 0) return DigestoDeEmail.Vazio;

        Trabalhando?.Invoke();

        var regras = RegrasDoVigia.Ler(_caminhoDasRegras);
        var lidas = new List<MensagemDeEmail>();
        var respondidas = new List<ThreadRespondida>();

        foreach (var caixa in caixas)
        {
            ct.ThrowIfCancellationRequested();

            string? senha = _cofre.Ler(caixa.Address);
            if (string.IsNullOrEmpty(senha)) continue;

            var endpoint = new ImapEndpoint(caixa.ImapHost, caixa.ImapPort, caixa.UseSsl);
            var guardado = _estado.Ler(caixa.Address);

            var doServidor = await _email.LerAsync(
                caixa.Address, senha, endpoint,
                DateTime.UtcNow.AddDays(-config.MailWindowDays),
                guardado, caixa.Address, ct).ConfigureAwait(false);

            lidas.AddRange(doServidor);
            GravarProgresso(caixa.Address, guardado, doServidor);

            // A pasta de enviados diz em que conversas ELE escreveu. Sai de graça, sem modelo,
            // e é o que sustenta "quem responde geralmente espera retorno".
            respondidas.AddRange(await _email.ThreadsRespondidasAsync(
                caixa.Address, senha, endpoint,
                DateTime.UtcNow.AddDays(-VigiasDoEmail.DiasDeVigia), ct).ConfigureAwait(false));
        }

        var vigiadas = VigiasDoEmail.Atualizar(_vigias.Ler(), respondidas, DateTime.UtcNow);
        _vigias.Gravar(vigiadas);

        var threadsVigiadas = new HashSet<string>(vigiadas.Select(v => v.Thrid), StringComparer.Ordinal);

        // A rajada é vista sobre o conjunto, e não caixa a caixa: o mesmo firewall pode estar
        // mandando para as duas contas, e contar separado esconderia metade do incidente.
        var rajadas = DetectorDeRajada.Encontrar(lidas, regras);

        var (sobem, descartadas) = PassarPeloFunil(lidas, regras, threadsVigiadas);

        // Sondagem sem rajada não tem por que acordar ninguém nem falar com o usuário: no
        // estado estável ela não encontra nada, e é justamente por isso que ela é barata.
        if (!comModelo && !Urgente(vigiadas, lidas, rajadas))
        {
            return rajadas.Count > 0
                ? Publicar(new DigestoDeEmail(Array.Empty<MailSummary>(), lidas.Count,
                                              descartadas, rajadas, DateTime.UtcNow))
                : DigestoDeEmail.Vazio;
        }

        var itens = await ResumirAsync(sobem, config, ct).ConfigureAwait(false);
        DigestosFeitos++;

        return Publicar(new DigestoDeEmail(itens, lidas.Count, descartadas, rajadas, DateTime.UtcNow));
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Se esta sondagem merece acordar o modelo antes da hora.
    /// <para>
    /// Rajada, sempre: um incidente às 9h14 descoberto no digest das 12h55 não vale nada. E
    /// resposta chegando numa conversa marcada com <c>acorda9b</c> — o campo existe para que
    /// essa decisão seja um DADO no arquivo, conferível, e não uma frase que o modelo
    /// interpretaria de um jeito hoje e de outro amanhã.
    /// </para>
    /// </summary>
    public static bool Urgente(
        IReadOnlyList<VigiaDeThread> vigiadas,
        IReadOnlyList<MensagemDeEmail> lidas,
        IReadOnlyList<Rajada> rajadas)
    {
        if (rajadas.Count > 0) return true;

        var acordam = new HashSet<string>(
            vigiadas.Where(v => v.Acorda9b).Select(v => v.Thrid), StringComparer.Ordinal);

        return acordam.Count > 0 && lidas.Any(m => acordam.Contains(m.ThreadId));
    }

    /// <summary>
    /// Degraus 0 e 1, sobre a lista inteira.
    /// <para>
    /// Separa o que sobe do que cai e guarda o porquê de cada queda. A lista de descartados é o
    /// que torna a triagem auditável — sem ela, confiar nela é fé.
    /// </para>
    /// </summary>
    public static (List<MensagemDeEmail> Sobem, List<Descartada> Caem) PassarPeloFunil(
        IEnumerable<MensagemDeEmail> mensagens, RegrasDoVigia regras,
        ISet<string>? vigiadas = null)
    {
        var sobem = new List<MensagemDeEmail>();
        var caem = new List<Descartada>();

        foreach (var m in mensagens ?? Array.Empty<MensagemDeEmail>())
        {
            var decisao = FiltroDeTriagem.Avaliar(m, regras, vigiadas);

            if (decisao.Sobe) sobem.Add(m);
            else caem.Add(new Descartada(m.De, m.Assunto, decisao.Motivo));
        }

        return (sobem, caem);
    }

    /// <summary>
    /// Degrau 3. Manda o lote ao modelo e casa cada veredito com a mensagem dele.
    /// <para>
    /// Mensagem que voltou SEM veredito não some: ganha um resumo de código com o próprio
    /// assunto e urgência média. O modelo esquecer uma linha do JSON não pode ser o mesmo que
    /// a mensagem não existir — é exatamente o falso negativo invisível que a regra 5 proíbe.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<MailSummary>> ResumirAsync(
        List<MensagemDeEmail> sobem, UserAppSettings config, CancellationToken ct)
    {
        if (sobem.Count == 0) return Array.Empty<MailSummary>();

        var lote = sobem
            .OrderByDescending(m => m.RecebidaUtc)
            .Take(TetoDoLote)
            .ToList();

        IReadOnlyList<VereditoDeEmail> vereditos = Array.Empty<VereditoDeEmail>();

        try
        {
            var provider = _provedores.GetProvider(ParaOModeloDoShadow(config));
            vereditos = await new TriadorDeEmail(provider).TriarAsync(lote, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Modelo fora do ar não pode apagar a caixa do usuário da tela. Sem veredito, todo
            // mundo desce com o resumo de código abaixo.
            Console.WriteLine($"[VIGIA] triagem sem modelo — {ex.GetType().Name}: {ex.Message}");
        }

        var porUid = vereditos.GroupBy(v => v.Uid).ToDictionary(g => g.Key, g => g.First());

        return lote
            .Select(m => porUid.TryGetValue(m.Uid, out var v)
                ? new MailSummary(m.Assunto, v.Resumo, v.Urgencia, "", m.De)
                : new MailSummary(m.Assunto, $"De {m.NomeDoRemetente}. O resumo não saiu desta vez.",
                                  MailUrgency.Media, "", m.De))
            .ToList();
    }

    /// <summary>
    /// As configurações com o modelo do Shadow no lugar do principal.
    /// <para>
    /// A triagem é trabalho de fundo e pode rodar num modelo menor que o da conversa. Trocar o
    /// campo numa CÓPIA, e não no objeto do cache, evita que uma triagem em andamento mude o
    /// modelo da conversa que o usuário está tendo.
    /// </para>
    /// </summary>
    private static UserAppSettings ParaOModeloDoShadow(UserAppSettings config)
    {
        var copia = config.Clone();

        if (!string.IsNullOrWhiteSpace(copia.ShadowModelName))
            copia.ModelName = copia.ShadowModelName;

        return copia;
    }

    /// <summary>
    /// Guarda até onde se leu, com a mesma regra da tela de configurações: janela vazia não
    /// zera o progresso, e selo trocado descarta o UID guardado em vez de preservar o maior.
    /// </summary>
    private void GravarProgresso(
        string endereco, EstadoDaCaixa? guardado, IReadOnlyList<MensagemDeEmail> doServidor)
    {
        if (doServidor.Count == 0) return;

        uint maior = doServidor.Max(m => m.Uid);

        _estado.Gravar(endereco, new EstadoDaCaixa
        {
            UidValidity = guardado?.UidValidity ?? 0,
            LastUid = guardado == null ? maior : Math.Max(guardado.LastUid, maior),
            LastReadUtc = DateTime.UtcNow
        });
    }

    private DigestoDeEmail Publicar(DigestoDeEmail digesto)
    {
        Console.WriteLine($"[VIGIA] {digesto.Lidas} lida(s), {digesto.Itens.Count} na tela, " +
                          $"{digesto.Descartadas.Count} descartada(s), {digesto.Rajadas.Count} rajada(s).");

        // Sondagem sem novidade não apaga o digest da manhã: o painel continua mostrando o
        // que ainda não foi tratado, em vez de esvaziar sozinho às 9h20.
        if (digesto.Itens.Count > 0 || digesto.Rajadas.Count > 0) Ultimo = digesto;

        if (digesto.TemAlgoADizer) Pronto?.Invoke(digesto);
        return digesto;
    }

    public void Dispose()
    {
        Parar();
        try { _parada.Cancel(); } catch { }
        _parada.Dispose();
    }
}
