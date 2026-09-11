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
    private readonly MarcoDoVigia _marco;
    private readonly DiarioDeTriagem _diario;
    private readonly ArquivoDeConversas _conversas;

    private readonly CancellationTokenSource _parada = new();
    private Timer? _relogio;
    private int _trabalhando;

    private DateTime? _ultimaSondagem;

    /// <summary>
    /// Por que a última batida não fez nada. Só vira linha de log quando MUDA.
    /// <para>
    /// Existe por um dia inteiro perdido: em sete horas o vigia não deu uma passada, com a chave
    /// ligada e dois horários de digest dentro da janela, e o registro de execução não tinha uma
    /// única linha explicando. Deu para provar pelos arquivos de estado que ele não rodou, e não
    /// deu para saber por quê — as três saídas de <see cref="BaterAsync"/> eram todas mudas.
    /// </para>
    /// <para>
    /// Uma linha por MUDANÇA de estado, e não por batida: a batida é de minuto em minuto, e
    /// anunciar "nada a fazer" 1.440 vezes por dia afogaria o log que ela deveria salvar.
    /// </para>
    /// </summary>
    private string _porqueParado = "";

    /// <summary>Quantas o modelo marcou como baixa na última passada. Vira linha auditável.</summary>
    private int _ignoradasPeloModelo;

    /// <summary>
    /// TODAS as triadas da última passada, inclusive as de urgência baixa. A tela recebe só o
    /// que pede ação; o diário recebe o conjunto inteiro, porque "quantos foram tratados hoje"
    /// é uma pergunta sobre o trabalho feito, e não sobre o que sobrou na tela.
    /// </summary>
    private IReadOnlyList<EmailTriado> _anotados = Array.Empty<EmailTriado>();

    public MailDigestService(
        SettingsService settings,
        IMailService email,
        IChatProviderFactory provedores,
        MailVault? cofre = null,
        EstadoDasCaixas? estado = null,
        Func<DateTime>? agora = null,
        string? caminhoDasRegras = null,
        VigiasDoEmail? vigias = null,
        string? raizDeDados = null,
        DiarioDeTriagem? diario = null,
        ArquivoDeConversas? conversas = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _email = email ?? throw new ArgumentNullException(nameof(email));
        _provedores = provedores ?? throw new ArgumentNullException(nameof(provedores));
        _cofre = cofre ?? new MailVault();
        _estado = estado ?? new EstadoDasCaixas();
        _agora = agora ?? (() => DateTime.Now);
        _caminhoDasRegras = caminhoDasRegras ?? RegrasDoVigia.CaminhoPadrao();
        _vigias = vigias ?? new VigiasDoEmail();
        _diario = diario ?? new DiarioDeTriagem(raizDeDados);
        _conversas = conversas ?? new ArquivoDeConversas(raizDeDados);
        _marco = new MarcoDoVigia(raizDeDados);
    }

    /// <summary>Um digest ficou pronto e tem algo a dizer.</summary>
    public event Action<DigestoDeEmail>? Pronto;

    /// <summary>Começou a trabalhar — o Shadow acende o anel enquanto isso dura.</summary>
    public event Action? Trabalhando;

    /// <summary>
    /// A passada acabou, tendo ela achado algo ou não.
    /// <para>
    /// Existe porque <see cref="Pronto"/> não serve para apagar o anel: ele só dispara quando o
    /// digest TEM ALGO A DIZER, e a passada silenciosa é o caso comum — uma sondagem sem rajada
    /// nem chega a publicar. Visto em produção: "1 lida(s), 0 na tela, 0 rajada(s)" e o anel do
    /// Shadow girando para sempre, porque o começo era incondicional e o fim não.
    /// </para>
    /// <para>
    /// Dispara no <c>finally</c>, e por isso vale também quando a leitura estoura no meio.
    /// Quem acende tem que apagar, inclusive quando dá errado.
    /// </para>
    /// </summary>
    public event Action? Terminou;

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
        if (Interlocked.Exchange(ref _trabalhando, 1) == 1)
        {
            // Passada anterior ainda de pé. Se for uma leitura demorada, é normal; se for uma
            // que travou, esta linha é o ÚNICO aviso de que o vigia emudeceu de vez.
            Anotar("a passada anterior ainda não terminou");
            return;
        }

        try
        {
            var config = _settings.LoadSettings();
            if (!config.ShadowHandlesMail)
            {
                Anotar("a chave 'Deixar o Shadow tratar os e-mails' está desligada");
                return;
            }

            var agora = _agora();

            // Uma leitura só do marco: ele é um arquivo em disco, e a batida é de minuto a
            // minuto.
            bool digest = AgendaDoVigia.HoraDoDigest(agora, _marco.UltimoDigest);
            bool sondagem = AgendaDoVigia.HoraDaSondagem(agora, _ultimaSondagem);

            if (!digest && !sondagem)
            {
                var proximo = _ultimaSondagem + AgendaDoVigia.IntervaloDaSondagem;
                Anotar($"fora de hora; próxima sondagem por volta de {proximo:HH:mm}");
                return;
            }

            Anotar("");

            if (digest)
            {
                // Gravado ANTES de rodar. Se o digest falhar no meio, ele não fica repetindo a
                // cada minuto até dar certo — espera o próximo horário, como faria alguém.
                _marco.GravarDigest(agora);
                _ultimaSondagem = agora;
                await ExecutarAsync(comModelo: true, _parada.Token).ConfigureAwait(false);
                return;
            }

            if (sondagem)
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
    /// Registra por que o vigia está parado, uma vez por mudança de motivo. Motivo vazio quer
    /// dizer "vai trabalhar agora", e volta a permitir o próximo aviso.
    /// </summary>
    private void Anotar(string motivo)
    {
        if (motivo == _porqueParado) return;

        _porqueParado = motivo;
        if (motivo.Length > 0) Console.WriteLine($"[VIGIA] parado: {motivo}.");
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

        try
        {
            return await PassadaAsync(config, caixas, comModelo, ct).ConfigureAwait(false);
        }
        finally
        {
            // O par do Invoke acima. Num finally porque a passada tem quatro saídas — sondagem
            // sem rajada, digest vazio, digest cheio e exceção — e três delas não passam pelo
            // Publicar.
            Terminou?.Invoke();
        }
    }

    private async Task<DigestoDeEmail> PassadaAsync(
        UserAppSettings config,
        IReadOnlyList<MailAccountSettings> caixas,
        bool comModelo,
        CancellationToken ct)
    {
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

            var leitura = await _email.LerAsync(
                caixa.Address, senha, endpoint,
                DateTime.UtcNow.AddDays(-config.MailWindowDays),
                guardado, caixa.Address, ct).ConfigureAwait(false);

            lidas.AddRange(leitura.Mensagens);
            GravarProgresso(caixa.Address, guardado, leitura);

            // A pasta de enviados diz em que conversas ELE escreveu. Sai de graça, sem modelo,
            // e é o que sustenta "quem responde geralmente espera retorno".
            var minhas = await _email.ThreadsRespondidasAsync(
                caixa.Address, senha, endpoint,
                DateTime.UtcNow.AddDays(-VigiasDoEmail.DiasDeVigia), ct).ConfigureAwait(false);

            respondidas.AddRange(minhas);

            // E agora também viram ESTADO: uma mensagem sua não é triada — não há o que decidir
            // sobre o que você mesmo escreveu — mas ela vira a vez da conversa, de "Nova
            // mensagem" para "Aguardando retorno". Sem isto, responder não mudava nada na tela.
            foreach (var minha in minhas)
            {
                if (string.IsNullOrWhiteSpace(minha.Thrid)) continue;

                // A hora vem da MENSAGEM, e não de agora: a passada roda de vinte em vinte
                // minutos, e carimbar tudo com o instante da varredura faria a conversa parecer
                // mais recente do que é — justamente o campo que a tela ordena.
                //
                // Uid 0 porque a mensagem enviada não tem uid na caixa de entrada. Ela conta
                // como UMA mensagem da conversa, e o dedupe por (uid, origem) impede que a
                // mesma resposta sua entre a cada passada.
                _conversas.Anotar(
                    ArquivoDeConversas.Chave(caixa.Address, minha.Thrid, 0),
                    new EntradaDaConversa(
                        ArquivoDeConversas.Agora(minha.QuandoUtc),
                        Uid: 0,
                        De: caixa.Address,
                        Assunto: "",
                        Minha: true,
                        Origem: "enviados"),
                    config.MailJournalDays);
            }
        }

        var vigiadas = VigiasDoEmail.Atualizar(_vigias.Ler(), respondidas, DateTime.UtcNow);
        _vigias.Gravar(vigiadas);

        var threadsVigiadas = new HashSet<string>(vigiadas.Select(v => v.Thrid), StringComparer.Ordinal);

        // TER HISTÓRICO é motivo de vigia. Sem isto, a resposta a uma conversa já triada podia
        // ser descartada pelo funil — mala-direta, envio em massa, remetente desconhecido — e o
        // arquivo da conversa ficaria parado sem ninguém notar, com a tela mostrando uma
        // contagem que parou de crescer e nenhum sinal de que algo foi perdido.
        foreach (string thrid in _conversas.ThreadsComHistorico())
            threadsVigiadas.Add(thrid);

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

        _ignoradasPeloModelo = 0;
        var itens = await ResumirAsync(sobem, config, ct).ConfigureAwait(false);
        DigestosFeitos++;

        if (_ignoradasPeloModelo > 0)
            descartadas.Add(new Descartada("(triagem)",
                $"{_ignoradasPeloModelo} mensagem(ns) lida(s) e sem pedido",
                "o modelo leu e concluiu que não pedem nada agora",
                _ignoradasPeloModelo));

        // Anota ANTES de publicar. Publicar dispara evento de interface; anotar é disco, e o
        // que a conversa vai consultar depois não pode depender de a tela ter aceitado o aviso.
        // As conversas paradas saem junto com o diário, pela mesma chave. O arquivo de e-mail é
        // APAGÁVEL, ao contrário do raw.jsonl da conversa com a IA.
        _conversas.Limpar(config.MailJournalDays);

        _diario.Gravar(
            new PassadaAnotada(
                DateTime.UtcNow.ToString("o"),
                lidas.Count,
                descartadas.Where(d => d.De != "(triagem)").Sum(d => d.Quantas),
                _anotados),
            config.MailJournalDays);

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

    // ─────────────────────────────────────────────────────────────────────────
    // Recarregar uma conversa — §3.11
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Relê uma conversa no servidor, refaz o resumo e ACRESCENTA o veredito ao histórico dela.
    /// <para>
    /// É o botão "Recarregar" de <c>tela-chat-v3.html §3.11</c>. Existe porque a passada
    /// automática só olha <c>MailWindowDays</c> para trás: uma conversa mais antiga que a janela
    /// nunca é revisitada sozinha, e o arquivo dela envelhece. Este é o caminho de volta, a
    /// pedido.
    /// </para>
    /// <para>
    /// ACRESCENTA, nunca sobrescreve. O veredito pode ter mudado — e guardar a mudança é o
    /// ponto: a regra 6 pede poder conferir quando a triagem mudou de ideia, e sobrescrever
    /// apagaria justamente isso.
    /// </para>
    /// <para>
    /// CUSTA UMA CHAMADA AO MODELO, e nesta máquina isso é minutos. Quem chama tem de
    /// desabilitar o botão enquanto roda — a spec já pede o ícone girando.
    /// </para>
    /// <para>
    /// O corpo desce para o modelo e morre aqui. Nada dele entra no arquivo: quem grava é o
    /// <see cref="ArquivoDeConversas"/>, e <see cref="EntradaDaConversa"/> não tem onde pôr.
    /// </para>
    /// </summary>
    /// <returns>O estado atualizado da conversa, ou <c>null</c> quando não deu para reler.</returns>
    public async Task<ArquivoDeConversas.Estado?> RecarregarConversaAsync(
        string conta, string threadId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(conta) || string.IsNullOrWhiteSpace(threadId)) return null;

        var config = _settings.LoadSettings();

        var caixa = (config.MailAccounts ?? new List<MailAccountSettings>())
            .FirstOrDefault(c => string.Equals(c.Address, conta, StringComparison.OrdinalIgnoreCase));

        if (caixa == null)
        {
            Console.WriteLine($"[VIGIA] recarregar: a caixa {conta} não está conectada.");
            return null;
        }

        string? senha = _cofre.Ler(caixa.Address);
        if (string.IsNullOrEmpty(senha))
        {
            Console.WriteLine($"[VIGIA] recarregar: sem senha no cofre para {conta}.");
            return null;
        }

        string chave = ArquivoDeConversas.Chave(conta, threadId, 0);

        var mensagens = await _email.LerConversaAsync(
            caixa.Address, senha, new ImapEndpoint(caixa.ImapHost, caixa.ImapPort, caixa.UseSsl),
            threadId, ct).ConfigureAwait(false);

        if (mensagens.Count == 0) return _conversas.EstadoDe(chave);

        IReadOnlyList<VereditoDeEmail> vereditos = Array.Empty<VereditoDeEmail>();

        try
        {
            vereditos = await new TriadorDeEmail(
                    _provedores.GetProvider(config), config.MailTriageThinking)
                .TriarAsync(mensagens, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Modelo fora do ar não pode apagar o que já se sabia da conversa. Sem veredito, o
            // histórico fica como estava e a tela mostra o de antes.
            Console.WriteLine($"[VIGIA] recarregar: triagem falhou — {ex.GetType().Name}: {ex.Message}");
            return _conversas.EstadoDe(chave);
        }

        var porUid = vereditos.GroupBy(v => v.Uid).ToDictionary(g => g.Key, g => g.First());

        foreach (var m in mensagens)
        {
            // Só o que NÃO é seu ganha veredito: não há o que triar no que você mesmo escreveu.
            bool minha = string.Equals(
                ConversaDeEmail.Endereco(m.De), conta, StringComparison.OrdinalIgnoreCase);

            bool achou = porUid.TryGetValue(m.Uid, out var v);
            bool temVeredito = !minha && achou;

            _conversas.Anotar(chave, new EntradaDaConversa(
                ArquivoDeConversas.Agora(m.RecebidaUtc),
                m.Uid, m.De, m.Assunto,
                temVeredito ? v.Urgencia.ToString() : "",
                temVeredito ? v.Resumo ?? "" : "",
                Minha: minha,
                Origem: "recarregar"), config.MailJournalDays);
        }

        return _conversas.EstadoDe(chave);
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

    /// Os endereços das caixas conectadas.
    /// <para>
    /// É contra eles que se decide "Nova mensagem" ou "Aguardando retorno": se quem escreveu
    /// por último foi você, a bola está com o outro lado. Sem a lista, TODA conversa apareceria
    /// como esperando resposta sua — inclusive as que você acabou de responder.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> EnderecosDoUsuario(UserAppSettings config) =>
        (config?.MailAccounts ?? new List<MailAccountSettings>())
            .Select(c => c.Address ?? "")
            .Where(e => e.Length > 0)
            .ToList();

    /// <summary>
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
            // O MODELO PRINCIPAL, e não o do Shadow. Visto em produção: com um 0.8b no campo, a
            // triagem classificou um cupom de marketing como MÁXIMA e escreveu um resumo que
            // não estava em lugar nenhum da mensagem. É exatamente o que a §"O degrau 2 é
            // invertido de propósito" prevê — modelo pequeno é confiante até quando erra, e por
            // isso ele nunca decide o que sobe. O degrau 3 é do 9B.
            var provider = _provedores.GetProvider(config);
            vereditos = await new TriadorDeEmail(provider, config.MailTriageThinking)
                .TriarAsync(lote, ct).ConfigureAwait(false);
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

        // A lista da tela é por CONVERSA, e não por mensagem. Cinco respostas da mesma thread
        // eram cinco itens repetindo o mesmo assunto; agora são um, com "5 respostas" e a data
        // da última. Ver tela-chat-v3.html §3.10.
        var conversas = ConversaDeEmail.Agrupar(lote, EnderecosDoUsuario(config));

        var todos = conversas
            .Select(c =>
            {
                var m = c.Recente;

                var baseDaLinha = porUid.TryGetValue(m.Uid, out var v)
                    ? new MailSummary(m.Assunto, v.Resumo, v.Urgencia, "", m.De)
                    : new MailSummary(m.Assunto, $"De {m.NomeDoRemetente}. O resumo não saiu desta vez.",
                                      MailUrgency.Media, "", m.De);

                string chave = ArquivoDeConversas.Chave(m.Conta, m.ThreadId, m.Uid);

                // O veredito desta mensagem vai para o histórico da conversa ANTES de a linha
                // ser montada: é dele que "3 respostas" passa a ser verdade na passada seguinte.
                _conversas.Anotar(chave, new EntradaDaConversa(
                    ArquivoDeConversas.Agora(m.RecebidaUtc),
                    m.Uid, m.De, m.Assunto,
                    baseDaLinha.Urgency.ToString(), baseDaLinha.Description,
                    Minha: false, Origem: "vigia"), config.MailJournalDays);

                // Derivado do ARQUIVO, nunca do lote. O agrupamento por lote só enxerga UMA
                // passada: a thread triada ontem que recebe resposta hoje chegaria como
                // "1 mensagem" numa conversa de três.
                var estado = _conversas.EstadoDe(chave);

                return baseDaLinha with
                {
                    LastMessageAt = (estado?.UltimaEm ?? m.RecebidaUtc).ToLocalTime(),
                    MessageCount = Math.Max(estado?.Mensagens ?? 0, c.Mensagens),
                    AwaitingMe = estado?.EsperandoVoce ?? c.EsperandoVoce,
                    ThreadId = m.ThreadId ?? ""
                };
            })
            .ToList();

        // BAIXA não vai para a tela. Visto em produção: de 21 mensagens triadas, o modelo
        // marcou 20 como baixa e acertou — e as 20 foram para o painel assim mesmo. Mostrar
        // tudo o que se leu é o oposto de triar; quem faz o trabalho e depois entrega a pilha
        // inteira de volta não entregou nada.
        //
        // Elas continuam CONTADAS e vão para a lista auditável: a regra 6 pede poder conferir
        // o que a triagem deixou de fora, e "o modelo achou que não pedia nada" é uma decisão
        // tão conferível quanto a do funil.
        _ignoradasPeloModelo = todos.Count(i => i.Urgency == MailUrgency.Baixa);

        // O diário fica com o lote INTEIRO — a de urgência baixa foi trabalho feito tanto
        // quanto a máxima, e sem ela a resposta a "quantos e-mails você tratou hoje" seria
        // menor que a verdade.
        _anotados = lote.Select(m => Anotar(m, porUid)).ToList();

        return todos.Where(i => i.Urgency != MailUrgency.Baixa).ToList();
    }

    /// <summary>
    /// A linha do diário de uma mensagem. Sem corpo — nunca. É a única coisa que sobrou da
    /// regra 3 como regra absoluta, e ela vive aqui: o objeto que vai para o disco não tem o
    /// campo, então não há descuido possível.
    /// </summary>
    private static EmailTriado Anotar(
        MensagemDeEmail m, IReadOnlyDictionary<uint, VereditoDeEmail> porUid)
    {
        bool teveVeredito = porUid.TryGetValue(m.Uid, out var v);

        return new EmailTriado(
            Remetente: m.De,
            Nome: m.NomeDoRemetente,
            Assunto: m.Assunto,
            Resumo: teveVeredito ? v.Resumo : "o resumo não saiu desta vez",
            Urgencia: (teveVeredito ? v.Urgencia : MailUrgency.Media).ToString().ToLowerInvariant(),
            Conta: m.Conta,
            RecebidaUtc: m.RecebidaUtc.ToString("o"));
    }

    /// <summary>
    /// Guarda até onde o VIGIA triou, sem tocar no ponteiro da contagem.
    /// <para>
    /// Os dois ponteiros são separados por um defeito visto em produção: eram o mesmo campo, e
    /// abrir a tela de configurações — que só conta — avançava o marcador. O vigia, rodando
    /// depois, encontrava a caixa "em dia" e não triava nada. Setecentas mensagens por ler
    /// viraram uma.
    /// </para>
    /// <para>
    /// Selo trocado descarta o ponteiro em vez de preservar o maior: numeração nova não se
    /// compara com a antiga. Leitura vazia não zera nada.
    /// </para>
    /// </summary>
    private void GravarProgresso(string endereco, EstadoDaCaixa? guardado, LeituraDaCaixa leitura)
    {
        if (leitura.UidValidity == 0) return;

        bool renumerou = guardado != null && guardado.UidValidity != leitura.UidValidity;

        uint maiorVisto = leitura.Mensagens.Count == 0 ? 0 : leitura.Mensagens.Max(m => m.Uid);

        uint triado = renumerou || guardado == null
            ? maiorVisto
            : Math.Max(guardado.LastTriagedUid, maiorVisto);

        _estado.Gravar(endereco, new EstadoDaCaixa
        {
            UidValidity = leitura.UidValidity,

            // O ponteiro da CONTAGEM é da tela; o vigia o carrega adiante sem alterá-lo, a não
            // ser que o selo tenha trocado — aí ele não vale mais para ninguém.
            LastUid = renumerou ? 0 : guardado?.LastUid ?? 0,
            LastReadUtc = guardado?.LastReadUtc,

            LastTriagedUid = triado,
            LastTriageUtc = DateTime.UtcNow
        });
    }

    private DigestoDeEmail Publicar(DigestoDeEmail digesto)
    {
        // MensagensDescartadas, e não Descartadas.Count: a linha da triagem vale por dezenas.
        Console.WriteLine($"[VIGIA] {digesto.Lidas} lida(s), {digesto.Itens.Count} na tela, " +
                          $"{digesto.MensagensDescartadas} descartada(s) em " +
                          $"{digesto.Descartadas.Count} linha(s), {digesto.Rajadas.Count} rajada(s).");

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
