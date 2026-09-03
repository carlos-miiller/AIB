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
    /// Primeira olhada na caixa: quantas mensagens há na janela de arranque e quantas estão
    /// por ler. NÃO baixa corpo de mensagem e NÃO marca nada como lido.
    /// </summary>
    Task<MailScanResult> VarrerAsync(
        string endereco,
        string senhaDeApp,
        ImapEndpoint endpoint,
        DateTime desdeUtc,
        uint uidDePartida,
        CancellationToken ct);
}

/// <summary>Resultado de uma varredura. Só números: nenhum assunto, nenhum remetente.</summary>
/// <param name="Mensagens">Quantas mensagens caíram na janela pedida.</param>
/// <param name="NaoLidas">Quantas dessas ainda não foram lidas pelo usuário.</param>
/// <param name="Novas">Quantas têm UID acima do <c>uidDePartida</c>, ou seja, chegaram depois.</param>
/// <param name="UidValidity">Selo de validade dos UIDs. Mudou? o último UID guardado virou lixo.</param>
/// <param name="UltimoUid">Maior UID visto, para a próxima varredura partir dele.</param>
public readonly record struct MailScanResult(
    bool Ok,
    int Mensagens,
    int NaoLidas,
    int Novas,
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
        DateTime desdeUtc, uint uidDePartida, CancellationToken ct)
        => Task.FromResult(new MailScanResult(false, 0, 0, 0, 0, 0, TextoPendente));

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
