using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

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
    private static readonly TimeSpan Paciencia = TimeSpan.FromSeconds(15);

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

    private static ImapClient NovoCliente() => new()
    {
        Timeout = (int)Paciencia.TotalMilliseconds
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
