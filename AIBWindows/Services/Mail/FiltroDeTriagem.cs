using System;
using System.Linq;

namespace AIB.Services.Mail;

/// <summary>O que o funil decidiu sobre uma mensagem, antes de qualquer modelo.</summary>
/// <param name="Sobe">Se ela merece o degrau 3 — o modelo que lê e resume.</param>
/// <param name="Motivo">Por que subiu ou caiu. Vai para a lista auditável de descartados.</param>
public readonly record struct DecisaoDoFunil(bool Sobe, string Motivo);

/// <summary>
/// Degraus 0 e 1 do funil — Gmail e regras. Custo zero, nenhum modelo.
/// <para>
/// O degrau 0 é o achado que mais economiza trabalho: as categorias do Gmail (Promoções,
/// Social, Atualizações, Fóruns) e o marcador <c>\Important</c> já são calculados no servidor,
/// de graça. Não há por que reimplementar heurística de newsletter.
/// </para>
/// <para>
/// ENVIESADO PARA RECALL, regra 5 do vigia: na dúvida, sobe. Um e-mail chato subindo custa três
/// segundos de leitura; um e-mail importante sumindo custa o que já se perde hoje — e some sem
/// aparecer em lugar nenhum.
/// </para>
/// </summary>
public static class FiltroDeTriagem
{
    /// <summary>Categorias do Gmail que o usuário não pediu para ver.</summary>
    private static readonly string[] CategoriasDescartaveis =
    {
        "category_promotions", "category_social", "category_updates", "category_forums"
    };

    /// <summary>
    /// Prefixos de remetente que não esperam resposta. Não bastam sozinhos: o firewall manda de
    /// <c>noreply@</c> e foi a coisa mais urgente do dia. Quem trata isso é a rajada.
    /// </summary>
    private static readonly string[] PrefixosAutomaticos =
    {
        "noreply", "no-reply", "nao-responda", "naoresponda", "donotreply", "mailer-daemon",
        "bounce", "notifications", "notificacao"
    };

    public static DecisaoDoFunil Avaliar(MensagemDeEmail msg, RegrasDoVigia regras)
    {
        if (msg == null) return new DecisaoDoFunil(true, "mensagem sem dados: na dúvida, sobe");

        // Fonte de alerta declarada pelo usuário nunca é descartada pela forma. Uma sozinha
        // ainda é ruído, mas quem decide isso é a contagem da rajada, não este degrau.
        if (regras?.Fonte(msg.De) != null)
            return new DecisaoDoFunil(true, "remetente é fonte de alerta no regras.md");

        // Degrau 0: o Google já disse que importa. Isso vence qualquer regra barata abaixo.
        if (msg.Importante)
            return new DecisaoDoFunil(true, "marcada como importante pelo Gmail");

        // Degrau 0: e já disse que não importa.
        var rotulos = (msg.Rotulos ?? Array.Empty<string>())
            .Select(r => (r ?? "").Trim().ToLowerInvariant())
            .ToArray();

        string? categoria = rotulos.FirstOrDefault(r => CategoriasDescartaveis.Contains(r));
        if (categoria != null)
            return new DecisaoDoFunil(false, $"o Gmail classificou como {Legivel(categoria)}");

        // Degrau 1: destinatário é pedido, cópia é notificação.
        if (msg.Direto)
            return new DecisaoDoFunil(true, "endereçada a você, não em cópia");

        if (EhAutomatico(msg.De))
            return new DecisaoDoFunil(false, "remetente automático e você só em cópia");

        // Sobra o que não se sabe. Regra 5: sobe.
        return new DecisaoDoFunil(true, "em cópia, mas de remetente que fala com gente");
    }

    /// <summary>
    /// Se o endereço é de caixa que não lê resposta. Olha só a parte ANTES do arroba: um
    /// domínio chamado <c>noreply.com.br</c> não faz de toda mensagem dele automática.
    /// </summary>
    public static bool EhAutomatico(string? endereco)
    {
        string e = (endereco ?? "").Trim().ToLowerInvariant();
        int arroba = e.IndexOf('@');
        string local = arroba > 0 ? e.Substring(0, arroba) : e;

        return PrefixosAutomaticos.Any(p => local.StartsWith(p, StringComparison.Ordinal));
    }

    private static string Legivel(string categoria) => categoria switch
    {
        "category_promotions" => "promoção",
        "category_social" => "rede social",
        "category_updates" => "atualização",
        "category_forums" => "fórum",
        _ => categoria
    };
}
