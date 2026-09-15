using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIB.Services.Mail;

/// <summary>Resultado de um teste de login — §9 passo 4.</summary>
/// <param name="Ok">O login passou.</param>
/// <param name="Endpoint">O candidato que respondeu, para gravar na conta.</param>
/// <param name="Erro">
/// Mensagem em linguagem de usuário, nunca a exceção do cliente IMAP (§3.12: "senha de app
/// recusada — altere a senha", não o stack do MailKit).
/// </param>
/// <param name="Verificado">
/// Se um servidor foi de fato contatado. <c>false</c> com <c>Ok = true</c> é o caso do
/// esqueleto: a conta é aceita mas nada foi confirmado, e a linha nasce em
/// <see cref="MailAccountStatus.Checking"/> em vez de verde.
/// </param>
public readonly record struct MailLoginResult(
    bool Ok,
    ImapEndpoint Endpoint,
    string Erro,
    bool Verificado);

/// <summary>
/// O contrato do serviço de e-mail. Só o teste de login por enquanto: a leitura da caixa
/// (§9 passo 4, <c>FetchAsync</c>) entra quando o vigia de e-mail for implementado, e declarar
/// o método agora só criaria uma assinatura para ninguém.
/// </summary>
public interface IMailService
{
    /// <summary>
    /// O serviço fala IMAP de verdade.
    /// <para>
    /// Existe porque a TELA precisa saber. Uma conta guardada e nunca lida tem duas causas
    /// completamente diferentes — o ciclo de leitura ainda não rodou, ou não existe ciclo de
    /// leitura nenhum — e a linha da conta ficava dizendo "ainda não lida nesta sessão" nos
    /// dois casos. A primeira frase promete uma leitura que vem; a segunda descreve um
    /// programa que não faz isso ainda. Confundir as duas faz o usuário esperar por algo que
    /// nunca vai acontecer e desconfiar da própria senha.
    /// </para>
    /// </summary>
    bool Disponivel { get; }

    /// <summary>
    /// Por que não está disponível, em linguagem de usuário. Vazio quando <see cref="Disponivel"/>.
    /// </summary>
    string MotivoDaIndisponibilidade { get; }

    Task<MailLoginResult> TestLoginAsync(string endereco, string senhaDeApp, CancellationToken ct);

    /// <summary>
    /// Olha a caixa. NÃO baixa corpo de mensagem e NÃO marca nada como lido.
    /// <para>
    /// <paramref name="guardado"/> é o que se sabia da caixa da última vez, e é o que decide o
    /// TAMANHO do trabalho: com ele válido, a busca vai direto aos UIDs acima do último lido;
    /// sem ele, cai na janela de <paramref name="desdeUtc"/>. Passar o estado inteiro, e não
    /// só o último UID, existe porque a decisão depende do selo de validade — e o selo de
    /// AGORA só aparece depois de abrir a pasta, quando quem chamou já respondeu.
    /// </para>
    /// </summary>
    Task<MailScanResult> VarrerAsync(
        string endereco,
        string senhaDeApp,
        ImapEndpoint endpoint,
        DateTime desdeUtc,
        EstadoDaCaixa? guardado,
        CancellationToken ct);

    /// <summary>
    /// Traz as mensagens COM conteúdo, para a triagem ler.
    /// <para>
    /// É a única porta por onde assunto, remetente e corpo entram no programa, e existe
    /// separada da varredura de propósito: a tela de configurações só conta, e contar não pode
    /// custar o download de nada. Quem chama isto é o vigia, e só quando o usuário ligou a
    /// triagem.
    /// </para>
    /// <para>
    /// Continua SOMENTE LEITURA: a INBOX abre em <c>EXAMINE</c> e os corpos descem com
    /// <c>BODY.PEEK</c>. Nada é marcado como lido.
    /// </para>
    /// </summary>
    /// <param name="enderecoDoUsuario">
    /// Para separar quem foi destinatário de quem só recebeu cópia. Cópia é notificação;
    /// destinatário é pedido, e o funil trata os dois de forma diferente.
    /// </param>
    Task<LeituraDaCaixa> LerAsync(
        string endereco,
        string senhaDeApp,
        ImapEndpoint endpoint,
        DateTime desdeUtc,
        EstadoDaCaixa? guardado,
        string enderecoDoUsuario,
        CancellationToken ct);

    /// <summary>
    /// As conversas em que o USUÁRIO escreveu, lidas da pasta de enviados.
    /// <para>
    /// Sai de graça e sem modelo nenhum: mensagem em <c>[Gmail]/Sent</c> traz a mesma
    /// <c>X-GM-THRID</c> da conversa. É o que sustenta a regra "quem responde geralmente espera
    /// retorno" — e é por ser IMAP puro que a decisão de interromper o usuário fica inteira na
    /// parte que não alucina.
    /// </para>
    /// <para>
    /// Desce apenas o identificador da conversa e a data. Nem assunto, nem corpo, nem
    /// destinatário: para saber que ele respondeu, nada disso é necessário.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ThreadRespondida>> ThreadsRespondidasAsync(
        string endereco,
        string senhaDeApp,
        ImapEndpoint endpoint,
        DateTime desdeUtc,
        CancellationToken ct);

    /// <summary>
    /// As mensagens de UMA conversa, pela <c>X-GM-THRID</c>.
    /// <para>
    /// É o que o botão "Recarregar" de <c>tela-chat-v3.html §3.11</c> precisa: relê a conversa
    /// no servidor e permite refazer o resumo. A leitura da passada é por JANELA — só olha
    /// <c>MailWindowDays</c> para trás —, então uma conversa mais antiga que isso nunca é
    /// revisitada sozinha. Este é o caminho de volta.
    /// </para>
    /// <para>
    /// Continua SOMENTE LEITURA, como todo o resto: a INBOX abre em <c>EXAMINE</c> e os corpos
    /// descem com <c>BODY.PEEK</c>. Nada é marcado como lido.
    /// </para>
    /// <para>
    /// Devolve vazio quando o provedor não expõe <c>X-GM-THRID</c>. Ali não existe "a conversa"
    /// para reler — cada mensagem é a própria — e inventar uma busca por assunto traria
    /// mensagens de outras pessoas com o mesmo título.
    /// </para>
    /// </summary>
    /// <para>
    /// MEMBRO PADRÃO: devolve vazio. Reler uma conversa só existe onde há <c>X-GM-THRID</c>, e
    /// quem não a implementa apenas não tem o botão "Recarregar" — não é erro. Sem o padrão,
    /// cada dublê de ensaio teria de escrever um método que nunca usa, e a interface passaria a
    /// cobrar de todos uma capacidade de um.
    /// </para>
    /// <param name="tetoDoCorpo">
    /// Quanto do corpo, já limpo, desce por mensagem. O padrão é o da triagem; a leitura do
    /// e-mail pela conversa (<c>mail_read</c>) pede mais, porque lê uma conversa e não um lote.
    /// </param>
    Task<IReadOnlyList<MensagemDeEmail>> LerConversaAsync(
        string endereco,
        string senhaDeApp,
        ImapEndpoint endpoint,
        string threadId,
        CancellationToken ct,
        int tetoDoCorpo = MensagemDeEmail.TetoDoCorpo)
        => Task.FromResult<IReadOnlyList<MensagemDeEmail>>(Array.Empty<MensagemDeEmail>());
}

/// <summary>
/// Resultado de uma varredura. Só números: nenhum assunto, nenhum remetente.
/// <para>
/// Não há campo "novas" separado de <paramref name="Mensagens"/>, e isso é de propósito: a
/// busca já nasce recortada para o que interessa. Na varredura incremental tudo o que voltou é
/// novo por construção; na varredura por data não se sabia nada da caixa antes, então tudo o
/// que voltou também é novo. Um segundo campo aqui seria sempre igual ao primeiro, e um dia
/// alguém acreditaria que não é.
/// </para>
/// </summary>
/// <param name="Mensagens">Quantas mensagens a busca trouxe.</param>
/// <param name="NaoLidas">Quantas dessas ainda não foram lidas pelo usuário.</param>
/// <param name="Incremental">
/// <c>true</c> quando a busca partiu do último UID lido; <c>false</c> quando releu a janela por
/// data. Quem mostra o número precisa saber disso: "3 novas" e "3 nos últimos 3 dias" são
/// frases diferentes, e trocar uma pela outra mente sobre o que foi olhado.
/// </param>
/// <param name="UidValidity">Selo de validade dos UIDs. Mudou? o último UID guardado virou lixo.</param>
/// <param name="UltimoUid">Maior UID visto, ou zero quando a busca não trouxe nada.</param>
/// <summary>
/// O que uma leitura trouxe.
/// <para>
/// O <paramref name="UidValidity"/> vem junto porque quem grava o progresso precisa dele: sem o
/// selo, o ponteiro de triagem gravado nunca casaria com o da caixa e toda leitura recomeçaria
/// pela data, retriando as mesmas mensagens para sempre.
/// </para>
/// </summary>
public readonly record struct LeituraDaCaixa(
    IReadOnlyList<MensagemDeEmail> Mensagens, uint UidValidity)
{
    public static LeituraDaCaixa Nada => new(Array.Empty<MensagemDeEmail>(), 0);
}

public readonly record struct MailScanResult(
    bool Ok,
    int Mensagens,
    int NaoLidas,
    bool Incremental,
    uint UidValidity,
    uint UltimoUid,
    string Erro);

/// <summary>
/// ESQUELETO. Não fala IMAP com ninguém — aceita a conta e a marca como NÃO verificada.
/// <para>
/// Existe porque esta entrega é a INTERFACE de tela-configuracoes, e o serviço de IMAP vem
/// depois. Um esqueleto que recusasse tudo deixaria a lista de §3.12 permanentemente vazia, e
/// a tela recém-desenhada não teria como ser olhada.
/// </para>
/// <para>
/// A §7 A15 manda só entrar na lista conta cujo login passou. Este esqueleto desvia disso, e o
/// desvio aparece NA TELA: a linha nasce em âmbar com "verificação pendente", nunca em verde.
/// Não há como confundir uma conta aceita pelo esqueleto com uma conta conectada.
/// </para>
/// </summary>
public sealed class MailServiceStub : IMailService
{
    public const string TextoPendente = "IMAP ainda não implementado — a leitura da caixa entra numa versão futura";

    public bool Disponivel => false;

    public string MotivoDaIndisponibilidade => TextoPendente;

    public Task<MailScanResult> VarrerAsync(
        string endereco, string senhaDeApp, ImapEndpoint endpoint,
        DateTime desdeUtc, EstadoDaCaixa? guardado, CancellationToken ct)
        => Task.FromResult(new MailScanResult(false, 0, 0, false, 0, 0, TextoPendente));

    public Task<LeituraDaCaixa> LerAsync(
        string endereco, string senhaDeApp, ImapEndpoint endpoint,
        DateTime desdeUtc, EstadoDaCaixa? guardado, string enderecoDoUsuario, CancellationToken ct)
        => Task.FromResult(LeituraDaCaixa.Nada);

    public Task<IReadOnlyList<ThreadRespondida>> ThreadsRespondidasAsync(
        string endereco, string senhaDeApp, ImapEndpoint endpoint,
        DateTime desdeUtc, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ThreadRespondida>>(Array.Empty<ThreadRespondida>());


    public Task<MailLoginResult> TestLoginAsync(string endereco, string senhaDeApp, CancellationToken ct)
    {
        var palpite = ImapHostGuesser.Primeiro(endereco);

        if (palpite == null)
            return Task.FromResult(new MailLoginResult(
                false,
                default,
                $"não encontramos o servidor de {endereco}",
                false));

        return Task.FromResult(new MailLoginResult(true, palpite.Value, "", Verificado: false));
    }
}
