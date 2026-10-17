using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AIB.Services;

namespace AIB.Ui;

/// <summary>
/// O texto do contador de tokens, igual no rodapé da janela de chat e sob a barra do orbe.
/// <para>
/// Morava na janela de chat, e o orbe ganhou um contador só com o número do prompt. Visto no
/// uso: no orbe a compactação não aparecia, sem o custo cru riscado e a seta que a janela
/// mostra.
/// </para>
/// </summary>
public static class ContadorDeTokens
{
    /// <summary>
    /// Escreve "<s>total</s> &gt; contexto tokens | teto | US$" em <paramref name="alvo"/>, na
    /// cor da ocupação.
    /// </summary>
    public static void Escrever(TextBlock alvo, TokenReport relatorio)
    {
        // A condição é o TOTAL diferir do contexto, e não haver economia. Uma conversa
        // reaberta sem capítulo nenhum não poupou nada, mas o custo cru dela continua sendo
        // maior que o contexto — e esconder isso é o que fazia 9.144 tokens virarem 1.838
        // sem explicação. Antes do primeiro capítulo os dois números são o mesmo, e escrever
        // "1.204 > 1.204" seria ocupar o espaço para não dizer nada.
        bool temTotal = relatorio.Total > relatorio.Contexto;

        alvo.Inlines.Clear();

        if (temTotal)
        {
            // TACHADO e apagado: é o preço que a conversa NÃO está pagando. Riscar diz isso
            // sem precisar de legenda, e deixa o número vivo ser o que salta aos olhos.
            alvo.Inlines.Add(new Run($"{relatorio.Total:N0}")
            {
                TextDecorations = TextDecorations.Strikethrough,
                Foreground = (System.Windows.Media.Brush)alvo.FindResource("TextMutedBrush")
            });

            // A seta fica. O risco diz que aquele preço não está sendo pago; a seta diz
            // que um número VIROU o outro. São duas informações, não uma repetida.
            alvo.Inlines.Add(new Run(" > "));
        }

        // O teto do nível no lugar da porcentagem: os dois números da esquerda já dizem quanto
        // foi poupado, e o dado que faltava é o quanto ainda cabe.
        alvo.Inlines.Add(new Run($"{relatorio.Contexto:N0} tokens | {relatorio.Max:N0}"));

        // O dinheiro, quando há. No Ollama o campo é nulo e o texto continua como sempre foi.
        if (relatorio.CustoUsd is decimal custo)
            alvo.Inlines.Add(new Run($" | {TokenReport.Dolares(custo)}"));

        alvo.Foreground = (System.Windows.Media.Brush)alvo.FindResource(relatorio.PincelDaOcupacao);
    }
}
