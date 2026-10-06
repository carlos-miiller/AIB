using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services.Mail;

/// <summary>O que o funil decidiu sobre uma mensagem, antes de qualquer modelo.</summary>
/// <param name="Sobe">Se ela merece o degrau 3 — o modelo que lê e resume.</param>
/// <param name="Motivo">Por que subiu ou caiu. Vai para a lista auditável de descartados.</param>
public readonly record struct DecisaoDoFunil(bool Sobe, string Motivo);

/// <summary>
/// Degraus 0 e 1 do funil — Gmail e regras. Custo zero, nenhum modelo.
/// <para>
/// O degrau 0 aproveita o que o servidor já calculou: o marcador <c>\Important</c> do Gmail e
/// os headers de lista que o próprio disparador põe. Nenhum dos dois custa uma linha de
/// heurística.
/// </para>
/// <para>
/// O QUE ELE NÃO TEM, e a primeira versão achou que tinha: as abas de categoria do Gmail.
/// Elas não existem sobre IMAP — o X-GM-LABELS traz <c>\Important</c>, <c>\Starred</c> e as
/// etiquetas do usuário, e nunca <c>CATEGORY_PROMOTIONS</c>. Aquele ramo passou uma execução
/// inteira sem disparar uma vez enquanto catorze mala-diretas subiam ao modelo. Quem faz o
/// trabalho agora é <see cref="MensagemDeEmail.EnvioEmMassa"/>.
/// </para>
/// <para>
/// ENVIESADO PARA RECALL, regra 5 do vigia: na dúvida, sobe. Um e-mail chato subindo custa três
/// segundos de leitura; um e-mail importante sumindo custa o que já se perde hoje — e some sem
/// aparecer em lugar nenhum.
/// </para>
/// </summary>
public static class FiltroDeTriagem
{
    /// <summary>
    /// Categorias do Gmail que o usuário não pediu para ver.
    /// <para>
    /// NÃO CHEGAM POR IMAP — ver a nota da classe. Ficam porque a API do Gmail as entrega com
    /// exatamente estes nomes, e o dia em que a leitura passar por ela o degrau volta a
    /// funcionar sozinho. Enquanto isso, quem descarta mala-direta é o header de lista.
    /// </para>
    /// </summary>
    private static readonly string[] CategoriasDescartaveis =
    {
        "category_promotions", "category_social", "category_updates", "category_forums"
    };

    /// <summary>
    /// Marcas de caixa que não lê resposta. Não bastam sozinhas: o firewall manda de
    /// <c>noreply@</c> e foi a coisa mais urgente do dia. Quem trata isso é a rajada, e as
    /// portas de fuga do <see cref="Avaliar"/> vêm antes desta checagem.
    /// </summary>
    private static readonly string[] MarcasAutomaticas =
    {
        "noreply", "no-reply", "nao-responda", "naoresponda", "donotreply", "do-not-reply",
        "mailer-daemon", "bounce", "notification", "notificacao", "newsletter", "marketing",
        "no_reply", "unsubscribe"
    };

    /// <param name="vigiadas">
    /// Conversas em que o usuário escreveu e ainda espera retorno. Uma resposta que chega numa
    /// delas passa na frente de qualquer regra barata: ele MESMO puxou aquele assunto, e um
    /// filtro que a descartasse estaria descartando a resposta que ele foi buscar.
    /// </param>
    public static DecisaoDoFunil Avaliar(
        MensagemDeEmail msg, RegrasDoVigia regras, ISet<string>? vigiadas = null)
    {
        if (msg == null) return new DecisaoDoFunil(true, "mensagem sem dados: na dúvida, sobe");

        if (vigiadas != null && msg.ThreadId.Length > 0 && vigiadas.Contains(msg.ThreadId))
            return new DecisaoDoFunil(true, "resposta numa conversa que você começou");

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

        // Degrau 0: o disparador se identificou. List-Unsubscribe é assinatura de mala-direta
        // — quem escreve para uma pessoa não oferece descadastro. Medido em produção: catorze
        // newsletters (Netflix, Udemy, Localiza, PicPay, 99, Patreon…) subiam ao modelo e
        // custavam 2.400 tokens de prefill, e nenhuma delas tinha marca no endereço: o sinal
        // estava no domínio e no header, nunca no local-part.
        if (msg.EnvioEmMassa)
            return new DecisaoDoFunil(false, "disparo para lista, com link de descadastro");

        // Degrau 1: caixa que não lê resposta não está pedindo nada.
        //
        // A checagem vem ANTES do "endereçada a você", e isso corrige um defeito visto em
        // produção: um e-mail de marketing da Wellhub, de no-reply@, endereçado diretamente ao
        // usuário, subia sem passar por aqui — e o modelo ainda o classificou como MÁXIMA. Ser
        // destinatário de um robô não é ser destinatário de um pedido.
        //
        // As três portas de fuga acima continuam abertas: conversa vigiada, fonte de alerta do
        // regras.md e \Important passam antes desta linha. É por elas que o alerta do firewall
        // e o aviso do banco continuam subindo.
        if (EhAutomatico(msg.De))
            return new DecisaoDoFunil(false, "remetente automático, que não espera resposta");

        // Destinatário é pedido, cópia é notificação.
        if (msg.Direto)
            return new DecisaoDoFunil(true, "endereçada a você, não em cópia");

        // Sobra o que não se sabe. Regra 5: sobe.
        return new DecisaoDoFunil(true, "em cópia, mas de remetente que fala com gente");
    }

    /// <summary>
    /// Se o endereço é de caixa que não lê resposta.
    /// <para>
    /// Procura a marca EM QUALQUER LUGAR da parte local, e não só no começo. Visto em
    /// produção: <c>messages-noreply@linkedin.com</c> e <c>updates-noreply@linkedin.com</c>
    /// passavam batido porque o endereço começa com "messages" e "updates" — a marca estava
    /// lá, no meio, e a checagem só olhava a primeira letra.
    /// </para>
    /// <para>
    /// Olha só a parte ANTES do arroba: um domínio chamado <c>noreply.com.br</c> não faz de
    /// toda mensagem dele automática, e uma pessoa que escreve de lá continua sendo uma pessoa.
    /// </para>
    /// </summary>
    public static bool EhAutomatico(string? endereco)
    {
        string e = (endereco ?? "").Trim().ToLowerInvariant();
        int arroba = e.IndexOf('@');
        string local = arroba > 0 ? e.Substring(0, arroba) : e;

        return MarcasAutomaticas.Any(m => local.Contains(m, StringComparison.Ordinal));
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
