using System;
using System.Collections.Generic;

namespace AIB.Services.Mail;

/// <summary>Um palpite de servidor: host, porta e se é SSL.</summary>
public readonly record struct ImapEndpoint(string Host, int Port, bool UseSsl);

/// <summary>
/// Deduz o servidor IMAP a partir do endereço — §9 passo 3 de tela-configuracoes.
/// <para>
/// A tela NÃO pergunta host, porta nem SSL: são três campos com uma resposta certa cada, e
/// perguntar é convidar erro de digitação. Não existe tela de servidor manual, e a §9 é
/// explícita: se a dedução falhar, o erro diz o que faltou — não se inventa um formulário.
/// </para>
/// </summary>
public static class ImapHostGuesser
{
    /// <summary>Domínios em que o host é sabido e não se tenta adivinhar.</summary>
    private static readonly Dictionary<string, string> Conhecidos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = "imap.gmail.com",
        ["googlemail.com"] = "imap.gmail.com",
        ["outlook.com"] = "outlook.office365.com",
        ["hotmail.com"] = "outlook.office365.com",
        ["live.com"] = "outlook.office365.com",
        ["yahoo.com"] = "imap.mail.yahoo.com",
        ["icloud.com"] = "imap.mail.me.com",
        ["me.com"] = "imap.mail.me.com",
        ["zoho.com"] = "imap.zoho.com"
    };

    private const int PortaPadrao = 993;

    /// <summary>
    /// Candidatos a tentar, em ordem, para o endereço dado. Vazio se o endereço não tem
    /// domínio utilizável.
    /// <para>
    /// Domínio conhecido devolve UM candidato. Domínio próprio devolve três, e a terceira é
    /// <c>imap.gmail.com</c> — não por preguiça, por medição: uma caixa de Google Workspace
    /// em domínio próprio (<c>@empresa.com.br</c>) não responde em <c>imap.empresa.com.br</c>
    /// nem em <c>mail.empresa.com.br</c>. O host dela é o do Gmail, e quem sabe disso pelo
    /// nome é o registro MX, não o domínio. Sem esta terceira tentativa a dedução falha
    /// exatamente na caixa corporativa, que é o caso que motivou o vigia de e-mail.
    /// </para>
    /// <para>
    /// O jeito certo é consultar o MX, e isso exige uma dependência de DNS que ainda não
    /// entrou. Até entrar, a terceira tentativa cobre o caso real com um palpite a mais.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ImapEndpoint> Candidatos(string endereco)
    {
        string dominio = DominioDe(endereco);
        if (dominio.Length == 0) return Array.Empty<ImapEndpoint>();

        if (Conhecidos.TryGetValue(dominio, out string? host))
            return new[] { new ImapEndpoint(host, PortaPadrao, true) };

        return new[]
        {
            new ImapEndpoint("imap." + dominio, PortaPadrao, true),
            new ImapEndpoint("mail." + dominio, PortaPadrao, true),
            new ImapEndpoint("imap.gmail.com", PortaPadrao, true)
        };
    }

    /// <summary>O primeiro candidato, para preencher a linha antes de qualquer teste.</summary>
    public static ImapEndpoint? Primeiro(string endereco)
    {
        var lista = Candidatos(endereco);
        return lista.Count == 0 ? null : lista[0];
    }

    /// <summary>
    /// Validação de formato do endereço — §3.12, campo 1, checada em LostFocus.
    /// <para>
    /// Deliberadamente frouxa: exatamente um <c>@</c>, algo antes, e um domínio com ponto e
    /// sem espaço. Regra de e-mail completa é território de RFC e recusa endereços válidos;
    /// quem valida de verdade é o servidor, no login.
    /// </para>
    /// </summary>
    public static bool EnderecoParecevalido(string endereco)
    {
        string limpo = (endereco ?? "").Trim();
        if (limpo.Length < 5) return false;
        if (limpo.IndexOf(' ') >= 0) return false;

        string[] partes = limpo.Split('@');
        if (partes.Length != 2) return false;
        if (partes[0].Length == 0) return false;

        string dominio = partes[1];
        int ponto = dominio.IndexOf('.');
        return ponto > 0 && ponto < dominio.Length - 1;
    }

    private static string DominioDe(string endereco)
    {
        if (!EnderecoParecevalido(endereco)) return "";
        return endereco.Trim().Split('@')[1].ToLowerInvariant();
    }
}
