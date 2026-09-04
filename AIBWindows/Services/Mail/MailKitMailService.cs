using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace AIB.Services.Mail;

/// <summary>
/// O cliente IMAP de verdade — §9 passo 4 de tela-configuracoes.
/// <para>
/// SOMENTE LEITURA, e não por disciplina: a INBOX é aberta com
/// <see cref="FolderAccess.ReadOnly"/>, que manda <c>EXAMINE</c> em vez de <c>SELECT</c>. Num
/// EXAMINE o servidor não altera flag nenhuma, então nem um <c>FETCH BODY[]</c> por engano
/// marcaria mensagem como lida. A regra 1 do vigia pede <c>BODY.PEEK</c> sempre; abrir a pasta
/// em modo de leitura é a mesma garantia um nível acima, onde ela não depende de nós
/// lembrarmos.
/// </para>
/// <para>
/// Além disso não se baixa corpo de mensagem: o <c>Fetch</c> pede envelope e flags, que são
/// <c>FETCH (UID FLAGS ENVELOPE)</c>. Nada do conteúdo entra em memória, em disco ou em log.
/// </para>
/// </summary>
public sealed class MailKitMailService : IMailService
{
    /// <summary>§9 passo 4: quinze segundos por caixa. Servidor mudo não segura a tela.</summary>
    private readonly TimeSpan _paciencia;

    /// <param name="segundosDeEspera">
    /// Quanto esperar por caixa. Configurável porque só importa em rede ruim — e é exatamente
    /// em rede ruim que quinze segundos deixam de bastar.
    /// </param>
    public MailKitMailService(int segundosDeEspera = UserAppSettings.PadraoDoTempoLimiteImapEmSegundos)
    {
        if (segundosDeEspera < 1) segundosDeEspera = 1;
        _paciencia = TimeSpan.FromSeconds(segundosDeEspera);
    }

    /// <summary>
    /// O que desce do servidor: identificador, flags e data de chegada —
    /// <c>FETCH (UID FLAGS INTERNALDATE)</c>. Nem corpo, nem assunto, nem remetente.
    /// </summary>
    private const MessageSummaryItems ItensDaVarredura =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate;

    public bool Disponivel => true;

    public string MotivoDaIndisponibilidade => "";

    // ─────────────────────────────────────────────────────────────────────────
    // Login
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tenta os candidatos de <see cref="ImapHostGuesser"/> em ordem e para no primeiro que
    /// autenticar.
    /// <para>
    /// Os três modos de falha que a §6.5 manda distinguir chegam do servidor como o mesmo tipo
    /// de erro genérico se ninguém os separar: IMAP desligado no Gmail, senha de app errada e
    /// conta bloqueada pelo admin. A escada abaixo separa pelo ESTÁGIO em que a coisa quebrou —
    /// conectar, autenticar ou abrir a INBOX — que é o único sinal confiável que existe.
    /// </para>
    /// </summary>
    public async Task<MailLoginResult> TestLoginAsync(string endereco, string senhaDeApp, CancellationToken ct)
    {
        var candidatos = ImapHostGuesser.Candidatos(endereco);

        if (candidatos.Count == 0)
            return new MailLoginResult(false, default, $"não encontramos o servidor de {endereco}", false);

        string ultimoErroDeAutenticacao = "";
        bool algumConectou = false;

        foreach (var candidato in candidatos)
        {
            ct.ThrowIfCancellationRequested();

            using var cliente = NovoCliente();

            try
            {
                await cliente.ConnectAsync(candidato.Host, candidato.Port,
                    candidato.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Host que não existe é o caso NORMAL aqui: a dedução tenta imap.{dominio} e
                // mail.{dominio} antes do Gmail, e num domínio de Workspace os dois primeiros
                // não respondem mesmo. Só vira erro se nenhum responder.
                Console.WriteLine($"[EMAIL] {candidato.Host}:{candidato.Port} não respondeu ({ex.GetType().Name}).");
                continue;
            }

            algumConectou = true;

            try
            {
                await cliente.AuthenticateAsync(endereco, senhaDeApp, ct).ConfigureAwait(false);
            }
            catch (AuthenticationException ex)
            {
                // A mensagem do servidor vai para o CONSOLE, nunca para a tela: o Gmail devolve
                // um texto longo com link de ajuda, e §3.12 pede linguagem de usuário na linha.
                Console.WriteLine($"[EMAIL] {candidato.Host}: autenticação recusada — {ex.Message}");
                ultimoErroDeAutenticacao = MensagemDeAutenticacao(ex.Message);
                await DesconectarAsync(cliente).ConfigureAwait(false);
                continue;
            }

            try
            {
                // Abrir a INBOX é o que separa "senha ok" de "IMAP habilitado". Uma conta pode
                // autenticar e ainda assim não ter acesso à pasta.
                await cliente.Inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EMAIL] {candidato.Host}: INBOX inacessível — {ex.Message}");
                await DesconectarAsync(cliente).ConfigureAwait(false);
                return new MailLoginResult(false, candidato,
                    "entrou, mas a caixa de entrada está inacessível — o IMAP pode estar desligado", false);
            }

            await DesconectarAsync(cliente).ConfigureAwait(false);
            return new MailLoginResult(true, candidato, "", Verificado: true);
        }

        if (!algumConectou)
            return new MailLoginResult(false, default,
                $"não encontramos o servidor de {endereco}", false);

        return new MailLoginResult(false, default,
            ultimoErroDeAutenticacao.Length > 0
                ? ultimoErroDeAutenticacao
                : "senha de app recusada — gere uma nova e tente de novo",
            false);
    }

    /// <summary>
    /// Traduz o que o servidor disse para uma frase acionável.
    /// <para>
    /// O Gmail é específico o suficiente para valer a leitura: ele avisa quando o IMAP está
    /// desligado e quando a senha comum foi usada no lugar da senha de app. As duas coisas têm
    /// conserto diferente, e mandar "senha recusada" nas duas manda o usuário para o lado errado.
    /// </para>
    /// </summary>
    public static string MensagemDeAutenticacao(string doServidor)
    {
        string m = (doServidor ?? "").ToLowerInvariant();

        if (m.Contains("imap access is disabled") || m.Contains("imap is disabled"))
            return "o IMAP está desligado nesta conta — ative em Gmail > Configurações > POP/IMAP";

        if (m.Contains("application-specific password") || m.Contains("app password"))
            return "esta conta exige senha de app — a senha normal não serve aqui";

        if (m.Contains("account has been disabled") || m.Contains("disabled by administrator"))
            return "conta bloqueada pelo administrador do domínio";

        return "senha de app recusada — gere uma nova e tente de novo";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Varredura
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<MailScanResult> VarrerAsync(
        string endereco,
        string senhaDeApp,
        ImapEndpoint endpoint,
        DateTime desdeUtc,
        EstadoDaCaixa? guardado,
        CancellationToken ct)
    {
        using var cliente = NovoCliente();

        try
        {
            await cliente.ConnectAsync(endpoint.Host, endpoint.Port,
                endpoint.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct)
                .ConfigureAwait(false);

            await cliente.AuthenticateAsync(endereco, senhaDeApp, ct).ConfigureAwait(false);

            // READONLY: EXAMINE em vez de SELECT. O servidor não muda flag nenhuma nesta sessão.
            var inbox = cliente.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            // O selo de validade de AGORA. Só aqui dá para saber se o estado guardado ainda
            // descreve esta caixa — por isso a decisão entre as duas buscas mora neste método, e
            // não em quem chamou.
            uint validade = inbox.UidValidity;
            bool incremental = guardado != null && guardado.ServeParaPartir(validade);

            var uids = incremental
                ? await inbox.SearchAsync(AcimaDe(guardado!.LastUid), ct).ConfigureAwait(false)
                // O SINCE conta em DIAS e pela hora do SERVIDOR, então a busca por data sai com
                // um dia de folga: pedir a data exata perderia mensagem na virada de fuso. O
                // excesso é aparado logo abaixo, pelo NaJanela.
                : await inbox.SearchAsync(SearchQuery.DeliveredAfter(desdeUtc.Date.AddDays(-1)), ct)
                    .ConfigureAwait(false);

            // FETCH (UID FLAGS INTERNALDATE). NÃO é BODY[] — nada do conteúdo desce, e nada é
            // marcado como lido.
            var resumos = uids.Count == 0
                ? new List<IMessageSummary>()
                : (await inbox.FetchAsync(uids, ItensDaVarredura, ct).ConfigureAwait(false)).ToList();

            // O aparo por data vale SÓ na busca por data. Na incremental, UID acima do último
            // lido JÁ significa "chegou depois", e filtrar por data ali esconderia mensagem
            // recebida agora com carimbo antigo — o que acontece em migração de caixa e em
            // APPEND. O critério da busca e o critério do corte têm de ser o mesmo.
            if (!incremental)
                resumos = resumos.Where(r => NaJanela(r.InternalDate, desdeUtc)).ToList();

            int naoLidas = resumos.Count(r => r.Flags.HasValue && !r.Flags.Value.HasFlag(MessageFlags.Seen));

            // Zero aqui significa "não vi nada", e NÃO "recomece do zero". Quem decide o que
            // guardar é o chamador.
            uint ultimoUid = resumos.Count == 0 ? 0 : resumos.Max(r => r.UniqueId.Id);

            await DesconectarAsync(cliente).ConfigureAwait(false);

            return new MailScanResult(true, resumos.Count, naoLidas, incremental, validade, ultimoUid, "");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EMAIL] {endereco}: varredura falhou — {ex.GetType().Name}: {ex.Message}");
            await DesconectarAsync(cliente).ConfigureAwait(false);
            return new MailScanResult(false, 0, 0, false, 0, 0, "não consegui ler a caixa agora");
        }
    }

    /// <summary>
    // ─────────────────────────────────────────────────────────────────────────
    // Leitura com conteúdo — o que a triagem lê
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O que desce quando o vigia vai TRIAR: além de identificador, flags e data, o envelope
    /// (remetente, destinatários, assunto), a estrutura do corpo e os dois campos que o Gmail
    /// dá de graça — a conversa e os rótulos.
    /// <para>
    /// Os rótulos são o degrau 0 do funil: Promoções, Social, Atualizações e Fóruns já vêm
    /// classificados pelo servidor, e o <c>\Important</c> também. Não há por que reimplementar
    /// heurística de newsletter aqui.
    /// </para>
    /// </summary>
    /// <summary>
    /// De quantas mensagens o corpo é baixado numa passada, das mais recentes para trás.
    /// <para>
    /// Não é economia de disco: é o tempo até o primeiro digest sair. Baixar corpo de tudo o
    /// que se acumulou desde a última leitura deixaria o vigia minutos preso no IMAP antes de
    /// acordar o modelo. E o que não desce não pode vazar.
    /// </para>
    /// </summary>
    public const int TetoDeCorpos = 40;

    private const MessageSummaryItems ItensDaTriagem =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Flags |
        MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope |
        MessageSummaryItems.BodyStructure |
        MessageSummaryItems.GMailThreadId | MessageSummaryItems.GMailLabels;

    public async Task<LeituraDaCaixa> LerAsync(
        string endereco,
        string senhaDeApp,
        ImapEndpoint endpoint,
        DateTime desdeUtc,
        EstadoDaCaixa? guardado,
        string enderecoDoUsuario,
        CancellationToken ct)
    {
        using var cliente = NovoCliente();

        try
        {
            await cliente.ConnectAsync(endpoint.Host, endpoint.Port,
                endpoint.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct)
                .ConfigureAwait(false);

            await cliente.AuthenticateAsync(endereco, senhaDeApp, ct).ConfigureAwait(false);

            var inbox = cliente.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            // Parte do ponteiro da TRIAGEM, não do da contagem. Eram o mesmo campo, e o
            // resultado era que abrir a tela de configurações contava as mensagens, avançava o
            // marcador e o vigia encontrava a caixa "em dia" sem ter triado nada.
            bool incremental = guardado != null && guardado.ServeParaTriar(inbox.UidValidity);

            var uids = incremental
                ? await inbox.SearchAsync(AcimaDe(guardado!.LastTriagedUid), ct).ConfigureAwait(false)
                : await inbox.SearchAsync(SearchQuery.DeliveredAfter(desdeUtc.Date.AddDays(-1)), ct)
                    .ConfigureAwait(false);

            if (uids.Count == 0)
            {
                await DesconectarAsync(cliente).ConfigureAwait(false);
                return new LeituraDaCaixa(Array.Empty<MensagemDeEmail>(), inbox.UidValidity);
            }

            var resumos = await inbox.FetchAsync(uids, ItensDaTriagem, ct).ConfigureAwait(false);

            var candidatas = resumos
                .Where(r => incremental || NaJanela(r.InternalDate, desdeUtc))
                .OrderByDescending(r => r.InternalDate ?? DateTimeOffset.MinValue)
                .ToList();

            var lidas = new List<MensagemDeEmail>();
            string eu = (enderecoDoUsuario ?? endereco ?? "").Trim().ToLowerInvariant();
            int comCorpo = 0;

            foreach (var r in candidatas)
            {
                ct.ThrowIfCancellationRequested();

                // O CORPO só desce das mais recentes. Uma caixa com 700 mensagens acumuladas
                // significaria 700 downloads antes de qualquer triagem começar — minutos de
                // IMAP para um lote que, no fim, leva 25 mensagens ao modelo. As demais entram
                // com envelope e flags, que é o que o funil usa para decidir; se alguma delas
                // subir, ela chega ao modelo com assunto e remetente, sem corpo.
                string corpo = comCorpo < TetoDeCorpos
                    ? await CorpoAsync(inbox, r, ct).ConfigureAwait(false)
                    : "";

                if (corpo.Length > 0) comCorpo++;
                lidas.Add(Converter(r, eu, corpo));
            }

            uint validade = inbox.UidValidity;
            await DesconectarAsync(cliente).ConfigureAwait(false);

            Console.WriteLine($"[VIGIA] {endereco}: {lidas.Count} mensagem(ns) para triar, " +
                              $"{comCorpo} com corpo. " +
                              (incremental
                                  ? $"a partir do UID {guardado!.LastTriagedUid}."
                                  : "primeira triagem, pela data."));

            return new LeituraDaCaixa(lidas, validade);
        }
        catch (OperationCanceledException)
        {
            await DesconectarAsync(cliente).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // O ENDEREÇO vai para o log; o conteúdo nunca. Regra 3 do vigia vale também para o
            // caminho de erro, que é onde é mais fácil esquecer dela.
            Console.WriteLine($"[VIGIA] {endereco}: leitura falhou — {ex.GetType().Name}: {ex.Message}");
            await DesconectarAsync(cliente).ConfigureAwait(false);
            return LeituraDaCaixa.Nada;
        }
    }

    /// <summary>
    /// O texto da mensagem, já encurtado.
    /// <para>
    /// Busca a parte de TEXTO, nunca o HTML nem os anexos: um e-mail de marketing tem 300 KB de
    /// HTML e nada dentro, e baixar anexo de uma caixa inteira para resumir seria trocar a
    /// leitura por um download.
    /// </para>
    /// <para>
    /// O MailKit pede a parte com <c>BODY.PEEK</c>, e a pasta já está em EXAMINE: dois níveis
    /// garantindo que ler não marque nada como lido.
    /// </para>
    /// </summary>
    private static async Task<string> CorpoAsync(IMailFolder inbox, IMessageSummary r, CancellationToken ct)
    {
        var parte = r.TextBody ?? r.HtmlBody;
        if (parte == null) return "";

        try
        {
            var entidade = await inbox.GetBodyPartAsync(r.UniqueId, parte, ct).ConfigureAwait(false);
            if (entidade is not TextPart texto) return "";

            // A limpeza vale para os DOIS: a parte de texto de um e-mail de marketing também
            // vem cheia de URL de rastreamento e de moldura de tabela em ASCII.
            return MensagemDeEmail.Encurtar(texto.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Corpo ilegível não pode derrubar a triagem das outras: a mensagem sobe só com
            // assunto e remetente, que já é mais do que não subir.
            return "";
        }
    }

    /// <summary>
    /// Tira as tags de um corpo em HTML. Grosseiro de propósito: o destino é um modelo que vai
    /// resumir, não um renderizador.
    /// </summary>
    public static string SemMarcacao(string? html)
    {
        string h = html ?? "";
        var sb = new System.Text.StringBuilder(h.Length);
        bool dentroDeTag = false;

        foreach (char c in h)
        {
            if (c == '<') { dentroDeTag = true; continue; }
            if (c == '>') { dentroDeTag = false; sb.Append(' '); continue; }
            if (!dentroDeTag) sb.Append(c);
        }

        // Espaço em excesso vira token em excesso, e o prefill é o que custa caro aqui.
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static MensagemDeEmail Converter(IMessageSummary r, string enderecoDoUsuario, string corpo)
    {
        var de = r.Envelope?.From?.Mailboxes?.FirstOrDefault();

        bool direto = r.Envelope?.To?.Mailboxes?
            .Any(m => string.Equals(m.Address, enderecoDoUsuario, StringComparison.OrdinalIgnoreCase))
            ?? false;

        var rotulos = r.GMailLabels?.ToArray() ?? Array.Empty<string>();

        return new MensagemDeEmail(
            Uid: r.UniqueId.Id,
            ThreadId: r.GMailThreadId?.ToString() ?? "",
            De: (de?.Address ?? "").Trim().ToLowerInvariant(),
            NomeDoRemetente: string.IsNullOrWhiteSpace(de?.Name) ? (de?.Address ?? "?") : de!.Name,
            Assunto: r.Envelope?.Subject ?? "(sem assunto)",
            RecebidaUtc: r.InternalDate?.UtcDateTime ?? DateTime.UtcNow,
            Direto: direto,
            Importante: rotulos.Any(l => string.Equals(l, "\\Important", StringComparison.OrdinalIgnoreCase)),
            Rotulos: rotulos,
            NaoLida: r.Flags.HasValue && !r.Flags.Value.HasFlag(MessageFlags.Seen),
            Corpo: corpo);
    }

    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // A pasta de enviados — quem espera retorno
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ThreadRespondida>> ThreadsRespondidasAsync(
        string endereco,
        string senhaDeApp,
        ImapEndpoint endpoint,
        DateTime desdeUtc,
        CancellationToken ct)
    {
        using var cliente = NovoCliente();

        try
        {
            await cliente.ConnectAsync(endpoint.Host, endpoint.Port,
                endpoint.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct)
                .ConfigureAwait(false);

            await cliente.AuthenticateAsync(endereco, senhaDeApp, ct).ConfigureAwait(false);

            // A pasta de enviados é pedida pelo PAPEL dela, e não pelo nome. "[Gmail]/Sent Mail"
            // muda com o idioma da conta — em português é "[Gmail]/E-mails enviados" —, e
            // procurar pelo nome quebraria em toda caixa que não estivesse em inglês.
            var enviados = cliente.GetFolder(SpecialFolder.Sent);
            if (enviados == null)
            {
                await DesconectarAsync(cliente).ConfigureAwait(false);
                return Array.Empty<ThreadRespondida>();
            }

            await enviados.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

            var uids = await enviados
                .SearchAsync(SearchQuery.DeliveredAfter(desdeUtc.Date.AddDays(-1)), ct)
                .ConfigureAwait(false);

            if (uids.Count == 0)
            {
                await DesconectarAsync(cliente).ConfigureAwait(false);
                return Array.Empty<ThreadRespondida>();
            }

            // Só a conversa e a data. Nem assunto, nem destinatário, nem corpo: para saber que
            // ele respondeu, nada disso é necessário — e o que não desce não vaza.
            var resumos = await enviados.FetchAsync(
                uids,
                MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate |
                MessageSummaryItems.GMailThreadId,
                ct).ConfigureAwait(false);

            var respondidas = resumos
                .Where(r => r.GMailThreadId.HasValue)
                .Select(r => new ThreadRespondida(
                    r.GMailThreadId!.Value.ToString(),
                    r.InternalDate?.UtcDateTime ?? DateTime.UtcNow))
                .ToList();

            await DesconectarAsync(cliente).ConfigureAwait(false);
            return respondidas;
        }
        catch (OperationCanceledException)
        {
            await DesconectarAsync(cliente).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Sem a pasta de enviados o vigia perde as conversas vigiadas, não a triagem. Uma
            // caixa que não expõe SPECIAL-USE continua sendo triada normalmente.
            Console.WriteLine($"[VIGIA] {endereco}: enviados ilegíveis — {ex.GetType().Name}: {ex.Message}");
            await DesconectarAsync(cliente).ConfigureAwait(false);
            return Array.Empty<ThreadRespondida>();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// A busca dos UIDs acima do último já lido — <c>SEARCH UID {n+1}:*</c>.
    /// <para>
    /// É o que faz a segunda visita à mesma caixa custar quase nada: sem isto, abrir a tela de
    /// configurações relê a janela inteira toda vez, e o estado guardado com tanto cuidado não
    /// poupa trabalho nenhum — só serve para rotular como "novo" o que já tinha sido baixado.
    /// </para>
    /// </summary>
    private static SearchQuery AcimaDe(uint ultimoLido)
    {
        // Saturar em vez de estourar: com o último UID no teto do uint, somar 1 daria zero e a
        // busca voltaria a caixa inteira. É impossível na prática e barato de garantir.
        uint primeiro = ultimoLido == uint.MaxValue ? uint.MaxValue : ultimoLido + 1;
        return SearchQuery.Uids(new UniqueIdRange(new UniqueId(primeiro), UniqueId.MaxValue));
    }

    /// <summary>
    /// Se a mensagem cabe mesmo na janela pedida.
    /// <para>
    /// Existe porque a busca no servidor é GROSSA de propósito: o <c>SINCE</c> conta em dias e
    /// leva um dia de folga para não perder nada na virada de fuso. Sem aparar o excesso aqui, a
    /// tela diria "3 dias" mostrando a contagem de quase cinco — e o número da tela tem de ser o
    /// número da janela.
    /// </para>
    /// <para>
    /// Mensagem sem data de chegada FICA. O servidor já a considerou dentro do intervalo, e
    /// descartá-la por falta de um campo opcional esconderia mensagem de verdade.
    /// </para>
    /// </summary>
    public static bool NaJanela(DateTimeOffset? recebido, DateTime desdeUtc)
    {
        if (recebido == null) return true;

        DateTime limite = desdeUtc.Kind == DateTimeKind.Utc ? desdeUtc : desdeUtc.ToUniversalTime();
        return recebido.Value.UtcDateTime >= limite;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private ImapClient NovoCliente() => new()
    {
        Timeout = (int)_paciencia.TotalMilliseconds
    };

    /// <summary>
    /// Desconecta sem deixar exceção escapar. Falhar ao dizer LOGOUT não desfaz o que já foi
    /// lido, e derrubar a varredura por causa disso trocaria um resultado bom por um erro.
    /// </summary>
    private static async Task DesconectarAsync(ImapClient cliente)
    {
        try
        {
            if (cliente.IsConnected) await cliente.DisconnectAsync(true).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
