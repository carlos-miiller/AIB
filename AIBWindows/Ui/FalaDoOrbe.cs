using System.Collections.Generic;
using System.Windows;
using AIB.Services;

namespace AIB.Ui;

/// <summary>
/// Uma fala na pilha acima da barra do orbe — §4.6 de shadow-assistant.html.
/// <para>
/// A pilha existe porque um balão só não dava conta: a mensagem que o usuário acabava de
/// mandar não aparecia em lugar nenhum, e a resposta se perdia ao fechar a barra. Com os dois
/// lados na mesma pilha, o que se lê acima da barra é a última troca inteira, e não metade
/// dela.
/// </para>
/// <para>
/// A pilha rola: não há teto de bolhas. O que a limita é a ALTURA do rolo, e não a contagem —
/// um teto de três apagava a pergunta que explicava a resposta ainda visível logo abaixo dela.
/// </para>
/// </summary>
public abstract class FalaDoOrbe
{
    protected FalaDoOrbe(string texto) => Texto = texto.Trim();

    public string Texto { get; }
}

/// <summary>O que o usuário mandou pela barra. Mesma bolha da janela de chat (§3.5).</summary>
public sealed class FalaDoUsuario : FalaDoOrbe
{
    public FalaDoUsuario(string texto) : base(texto) { }
}

/// <summary>
/// O que a IA respondeu ou avisou. Leva, no caso do relatório de e-mail (§5.7), a lista
/// abaixo do texto — dentro do MESMO balão, nunca um segundo.
/// <para>
/// Não leva nome nem hora: o cabeçalho saiu do balão. Quatro elementos de moldura em volta
/// de uma frase de dez palavras pesavam mais que a frase, e quem está falando já se sabe pelo
/// lado e pelo fundo da bolha.
/// </para>
/// </summary>
public sealed class FalaDaIA : FalaDoOrbe
{
    /// <summary>A11 — no máximo três itens e SEM rolagem; o resto vira uma linha de texto.</summary>
    public const int TetoDeEmails = 3;

    public FalaDaIA(string texto, IReadOnlyList<MailSummary>? emails = null) : base(texto)
    {
        var mostrados = new List<MailSummary>();
        if (emails != null)
            for (int i = 0; i < emails.Count && i < TetoDeEmails; i++)
                mostrados.Add(emails[i]);

        Emails = mostrados;

        int sobra = (emails?.Count ?? 0) - mostrados.Count;
        Excedente = sobra <= 0 ? "" : sobra == 1 ? "+1 outro" : $"+{sobra} outros";
    }

    public IReadOnlyList<MailSummary> Emails { get; }

    /// <summary>"+4 outros", ou vazio quando tudo coube.</summary>
    public string Excedente { get; }

    public Visibility VisibilidadeDaLista =>
        Emails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VisibilidadeDoExcedente =>
        Excedente.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}
