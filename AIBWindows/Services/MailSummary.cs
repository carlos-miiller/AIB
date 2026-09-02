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
/// <param name="Account">De qual caixa veio. Duas contas exigem dizer qual.</param>
public sealed record MailSummary(
    string Name,
    string Description,
    MailUrgency Urgency,
    string Url = "",
    string Account = "");
