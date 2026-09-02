using System;
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
/// A pilha NÃO é o histórico da conversa: isso é da janela de chat (§5.3). Ela guarda as três
/// falas mais recentes e nada mais — o teto da própria §4.6.
/// </para>
/// </summary>
public abstract class FalaDoOrbe
{
    protected FalaDoOrbe(string texto)
    {
        Texto = texto.Trim();
        Hora = DateTime.Now.ToString("HH:mm");
    }

    public string Texto { get; }

    public string Hora { get; }
}

/// <summary>O que o usuário mandou pela barra. Mesma bolha da janela de chat (§3.5).</summary>
public sealed class FalaDoUsuario : FalaDoOrbe
{
    public FalaDoUsuario(string texto) : base(texto) { }
}

/// <summary>
/// O que a IA respondeu ou avisou. Leva o nome do personagem no cabeçalho e, no caso do
/// relatório de e-mail (§5.7), a lista abaixo do texto — dentro do MESMO balão, nunca um
/// segundo.
/// </summary>
public sealed class FalaDaIA : FalaDoOrbe
{
    /// <summary>A11 — no máximo três itens e SEM rolagem; o resto vira uma linha de texto.</summary>
    public const int TetoDeEmails = 3;

    public FalaDaIA(string texto, string nome, IReadOnlyList<MailSummary>? emails = null) : base(texto)
    {
        Nome = nome;

        var mostrados = new List<MailSummary>();
        if (emails != null)
            for (int i = 0; i < emails.Count && i < TetoDeEmails; i++)
                mostrados.Add(emails[i]);

        Emails = mostrados;

        int sobra = (emails?.Count ?? 0) - mostrados.Count;
        Excedente = sobra <= 0 ? "" : sobra == 1 ? "+1 outro" : $"+{sobra} outros";
    }

    public string Nome { get; }

    public IReadOnlyList<MailSummary> Emails { get; }

    /// <summary>"+4 outros", ou vazio quando tudo coube.</summary>
    public string Excedente { get; }

    public Visibility VisibilidadeDaLista =>
        Emails.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VisibilidadeDoExcedente =>
        Excedente.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}
