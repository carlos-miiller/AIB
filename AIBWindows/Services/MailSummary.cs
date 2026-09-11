using System;

namespace AIB.Services;

/// <summary>Quanto um e-mail pede atenção — shadow-assistant.html §4.8.</summary>
public enum MailUrgency
{
    Baixa,
    Media,
    Maxima
}

/// <summary>
/// Um e-mail no relatório do orbe — §4.8 / §7.
/// <para>
/// Modelo de exibição, não de transporte: já chega triado e resumido. Corpo de mensagem não
/// entra aqui e não vai para lugar nenhum que persista — o <c>raw.jsonl</c> guardaria e-mail
/// alheio, e o resumidor de capítulos o leria semanas depois.
/// </para>
/// </summary>
/// <param name="Name">
/// Assunto ou remetente, como veio. Uma linha, com reticências quando não couber. Sem prefixo
/// inventado: o que o usuário lê tem de bater com o que ele vai achar na caixa.
/// </param>
/// <param name="Description">Resumo gerado pela IA. No máximo duas linhas na tela.</param>
/// <param name="Urgency">Nível, que decide a cor da barra e do selo.</param>
/// <param name="Url">
/// Endereço que abre a mensagem. As duas caixas do usuário são webmail, então é uma URL do
/// Gmail com a thread — não há cliente de e-mail para invocar. Vazio desativa o clique.
/// </param>
/// <param name="Account">
/// De qual CAIXA veio — o endereço da conta conectada, nunca o do remetente. É por ele que se
/// acha a conta em <c>MailAccounts</c> para reler a conversa (§3.11) e é dele que sai o rótulo
/// "Abrir no Gmail"/"Abrir no Outlook". Duas contas exigem dizer qual.
/// </param>
/// <param name="LastMessageAt">
/// Data da mensagem mais RECENTE da conversa — não a do primeiro e-mail. Uma conversa de cinco
/// dias mostrando a data de abertura diria que nada aconteceu desde então.
/// </param>
/// <param name="MessageCount">
/// Tamanho da conversa: quantas mensagens ela tem. Um vira "1 mensagem"; mais, "N respostas".
/// </param>
/// <param name="AwaitingMe">
/// A última palavra é do outro lado, então a resposta é sua. Sai de comparar o remetente da
/// última mensagem com os endereços da conta conectada — NÃO é campo do servidor.
/// </param>
/// <param name="ContextTokens">Peso do resumo no prompt. Zero quando ainda não foi medido.</param>
/// <param name="ThreadId">
/// A conversa a que pertence (X-GM-THRID). Vazio em provedor que não o expõe, e aí cada
/// mensagem é a própria conversa. É a chave que liga a triagem gravada em disco à lista.
/// </param>
/// <param name="De">
/// Quem escreveu a mensagem mais recente, como veio: <c>"Fulano &lt;f@x.com&gt;"</c> ou só o
/// endereço. É o que o corpo do acordeão mostra e o que abre o cartão de §3.11.
/// <para>
/// Separado de <see cref="Account"/> de propósito: os dois já foram o mesmo campo, e o rótulo
/// do botão secundário passou a seguir o provedor de QUEM MANDOU em vez do da caixa — uma
/// mensagem do Gmail numa conta Outlook prometia "Abrir no Gmail".
/// </para>
/// </param>
public sealed record MailSummary(
    string Name,
    string Description,
    MailUrgency Urgency,
    string Url = "",
    string Account = "",
    DateTime LastMessageAt = default,
    int MessageCount = 1,
    bool AwaitingMe = true,
    int ContextTokens = 0,
    string ThreadId = "",
    string De = "");
