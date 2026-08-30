using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

// O projeto referencia System.Drawing e WinForms; estes nomes existem nos dois mundos.
using Size = System.Windows.Size;
using FontFamily = System.Windows.Media.FontFamily;
using FlowDirection = System.Windows.FlowDirection;
using Brushes = System.Windows.Media.Brushes;

namespace AIB.Ui;

/// <summary>
/// Mede a largura NATURAL de um <see cref="FlowDocument"/> — a largura que o texto ocuparia se
/// ninguém o obrigasse a quebrar linha.
/// <para>
/// Existe porque não dá para perguntar isso ao próprio controle. Um
/// <see cref="System.Windows.Controls.FlowDocumentScrollViewer"/> é feito para paginar: ele
/// aceita toda a largura oferecida e devolve exatamente ela como largura desejada, inclusive
/// quando se mede com largura infinita. Medir o documento por fora é o único jeito de saber
/// quanto o conteúdo realmente quer.
/// </para>
/// <para>
/// A conta é por parágrafo: a largura de um parágrafo é a soma dos seus trechos, porque eles
/// são desenhados um após o outro na mesma linha. A largura do documento é a do parágrafo mais
/// largo. Quebra de linha explícita reinicia a soma.
/// </para>
/// <para>
/// Erro para mais é inofensivo — quem chama usa o MENOR entre a natural e a disponível, então
/// sobrestimar só devolve o comportamento antigo de ocupar tudo. Erro para menos cortaria
/// texto, e é por isso que a medição segue as propriedades de fonte de cada trecho em vez de
/// assumir uma média.
/// </para>
/// </summary>
public static class FlowDocumentMeasure
{
    /// <summary>Largura natural do documento, ou 0 quando não há o que medir.</summary>
    public static double LarguraNatural(FlowDocument? documento)
    {
        if (documento == null) return 0;

        double maior = 0;
        foreach (var bloco in documento.Blocks)
            maior = Math.Max(maior, LarguraDoBloco(bloco, documento));

        return maior;
    }

    /// <summary>
    /// Troca "automático" por zero.
    /// <para>
    /// Margem e recuo de bloco num <see cref="FlowDocument"/> nascem como <c>Auto</c>, que o
    /// WPF representa como <see cref="double.NaN"/> — é assim que o documento sabe que pode
    /// aplicar o espaçamento padrão dele. Somar isso contamina a conta inteira: qualquer
    /// operação com NaN devolve NaN, e a medição toda vira NaN sem nenhum erro no caminho.
    /// </para>
    /// </summary>
    private static double Finito(double valor) =>
        double.IsNaN(valor) || double.IsInfinity(valor) ? 0 : valor;

    private static double LarguraDoBloco(Block bloco, FlowDocument documento)
    {
        double recuo = Finito(bloco.Margin.Left) + Finito(bloco.Margin.Right)
                     + Finito(bloco.Padding.Left) + Finito(bloco.Padding.Right);

        switch (bloco)
        {
            case Paragraph paragrafo:
                return recuo + LarguraDosInlines(paragrafo.Inlines, documento);

            case List lista:
            {
                double maior = 0;
                foreach (var item in lista.ListItems)
                    foreach (var interno in item.Blocks)
                        maior = Math.Max(maior, LarguraDoBloco(interno, documento));

                // O marcador do item e o recuo da lista não entram na medição dos blocos.
                // MarkerOffset também nasce Auto/NaN.
                return recuo + Finito(lista.MarkerOffset) + 24 + maior;
            }

            case Section secao:
            {
                double maior = 0;
                foreach (var interno in secao.Blocks)
                    maior = Math.Max(maior, LarguraDoBloco(interno, documento));
                return recuo + maior;
            }

            case Table tabela:
                // Tabela tem largura própria por coluna e raramente encolhe bem. Devolver
                // infinito faz quem chama escolher a largura disponível, que é o certo aqui.
                _ = tabela;
                return double.PositiveInfinity;

            case BlockUIContainer container:
                container.Child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return recuo + (container.Child?.DesiredSize.Width ?? 0);

            default:
                return recuo;
        }
    }

    private static double LarguraDosInlines(InlineCollection inlines, FlowDocument documento)
    {
        double linhaAtual = 0;
        double maior = 0;

        foreach (var inline in inlines)
        {
            if (inline is LineBreak)
            {
                maior = Math.Max(maior, linhaAtual);
                linhaAtual = 0;
                continue;
            }

            linhaAtual += LarguraDoInline(inline, documento);
        }

        return Math.Max(maior, linhaAtual);
    }

    private static double LarguraDoInline(Inline inline, FlowDocument documento)
    {
        switch (inline)
        {
            case Run corrida:
                return LarguraDoTexto(corrida.Text, corrida, documento);

            case Span span:
                // Bold, Italic, Hyperlink e Span puro caem todos aqui: o conteúdo deles é uma
                // coleção de inlines desenhada em sequência.
                return LarguraDosInlines(span.Inlines, documento);

            case InlineUIContainer container:
                container.Child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return container.Child?.DesiredSize.Width ?? 0;

            default:
                return 0;
        }
    }

    private static double LarguraDoTexto(string? texto, TextElement dono, FlowDocument documento)
    {
        if (string.IsNullOrEmpty(texto)) return 0;

        // Propriedade não definida no trecho cai para a do documento — é o mesmo caminho que o
        // WPF usa para herdar fonte.
        var familia = dono.FontFamily ?? documento.FontFamily ?? new FontFamily("Segoe UI");
        double corpo = dono.FontSize > 0 ? dono.FontSize
                     : documento.FontSize > 0 ? documento.FontSize : 14.0;

        var tipografia = new Typeface(
            familia,
            dono.FontStyle,
            dono.FontWeight,
            dono.FontStretch);

        var medida = new FormattedText(
            texto,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            tipografia,
            corpo,
            Brushes.Black,
            new NumberSubstitution(),
            TextFormattingMode.Ideal,
            1.0);

        return medida.WidthIncludingTrailingWhitespace;
    }
}
