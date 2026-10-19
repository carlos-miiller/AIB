using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AIB.Services.Documentos;
using AIB.Ui;

using Brush = System.Windows.Media.Brush;

namespace AIB.Views;

/// <summary>
/// O plano de gravação de documento no cartão — mockup "Edição de documentos do AIB"
/// (19/10/2026): célula, antes e depois; linhas que saem e entram; o efeito nas fórmulas que já
/// existiam; e a cópia guardada antes de gravar.
/// </summary>
public partial class ConfirmCardView
{
    /// <summary>Desenha o plano. Chamado pelo <see cref="Preencher"/> quando há um.</summary>
    private void MostrarPlano(PlanoDeGravacao plano)
    {
        PlanoPanel.Visibility = Visibility.Visible;
        PlanoResumoText.Text = plano.Resumo;
        PlanoResumoText.Visibility = plano.Resumo.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var c in plano.Contagens)
        {
            PlanoContagens.Children.Add(new Border
            {
                Background = Pincel("SurfaceCodeBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = c, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Pincel("TextSecondaryBrush") }
            });
        }

        if (plano.Mudancas.Count > 0) MontarTabela(plano);
        else if (plano.Previa != null) MontarPrevia(plano.Previa);

        if (plano.Efeitos.Count > 0)
        {
            PlanoEfeitos.Visibility = Visibility.Visible;
            PlanoEfeitos.Children.Add(Linha("Efeito nas fórmulas que já existem:", "TextSecondaryBrush"));
            foreach (var e in plano.Efeitos) PlanoEfeitos.Children.Add(Linha(e, "AccentBlueBrush", mono: true));
        }

        if (plano.Avisos.Count > 0)
        {
            PlanoAvisos.Visibility = Visibility.Visible;
            foreach (var a in plano.Avisos)
            {
                var t = Linha(a, "WarnBrush");
                t.Margin = new Thickness(0, 8, 0, 0);
                PlanoAvisos.Children.Add(t);
            }
        }

        if (plano.Copia != null)
        {
            PlanoCopiaText.Text = $"Antes de gravar, o original é guardado em {plano.Copia} (as 5 últimas versões, por 3 meses).";
            PlanoCopiaText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>A tabela: onde, antes, depois. Grupo e "+ N" ocupam a linha inteira.</summary>
    private void MontarTabela(PlanoDeGravacao plano)
    {
        PlanoTabelaBorder.Visibility = Visibility.Visible;
        var g = PlanoTabela;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int linha = 0;
        void Nova() => g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Nova();
        Cabecalho("ONDE", 0);
        Cabecalho("ANTES", 1);
        Cabecalho("DEPOIS", 2);

        foreach (var m in plano.Mudancas)
        {
            linha++;
            Nova();

            switch (m.Tipo)
            {
                case TipoDeMudanca.Grupo:
                    Inteira(m.Onde, "TextStrongBrush", FontWeights.SemiBold, null);
                    break;

                case TipoDeMudanca.Mais:
                    Inteira(m.Onde, "TextMutedBrush", FontWeights.Normal, null, centro: true);
                    break;

                case TipoDeMudanca.Sai:
                    Inteira("− " + m.Onde + "   " + m.Antes, "DangerTextBrush", FontWeights.Normal, "DangerFill12Brush", mono: true);
                    break;

                case TipoDeMudanca.Entra:
                    Inteira("+ " + m.Onde + "   " + m.Depois, "SuccessBrush", FontWeights.Normal, "SuccessFill10Brush", mono: true);
                    break;

                default:
                    Celula(m.Onde, 0, "TextSecondaryBrush");
                    Celula(m.Antes ?? "vazia", 1, m.Antes == null ? "TextMutedBrush" : "DangerTextBrush", italico: m.Antes == null);
                    Celula(m.Depois ?? "vazia", 2, m.Depois == null ? "TextMutedBrush" : m.Erro ? "DangerTextBrush" : "SuccessBrush",
                           italico: m.Depois == null);
                    break;
            }
        }

        void Cabecalho(string texto, int col)
        {
            var t = new TextBlock
            {
                Text = texto, FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = Pincel("TextMutedBrush"), Margin = new Thickness(0, 4, 12, 4)
            };
            Grid.SetRow(t, 0);
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        void Celula(string texto, int col, string cor, bool italico = false)
        {
            var t = Texto(texto, cor, mono: true);
            t.Margin = new Thickness(0, 4, 12, 4);
            if (italico) t.FontStyle = FontStyles.Italic;
            Grid.SetRow(t, linha);
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }

        void Inteira(string texto, string cor, FontWeight peso, string? fundo, bool centro = false, bool mono = false)
        {
            var t = Texto(texto, cor, mono);
            t.FontWeight = peso;
            t.TextAlignment = centro ? TextAlignment.Center : TextAlignment.Left;
            var b = new Border
            {
                Child = t,
                Padding = new Thickness(4, 4, 4, 4),
                Margin = new Thickness(-4, 0, -4, 0),
                Background = fundo == null ? null : Pincel(fundo)
            };
            Grid.SetRow(b, linha);
            Grid.SetColumnSpan(b, 3);
            g.Children.Add(b);
        }
    }

    /// <summary>A prévia de arquivo novo: as abas e as primeiras linhas, separadas por │.</summary>
    private void MontarPrevia(PreviaDeArquivo previa)
    {
        PlanoTabelaBorder.Visibility = Visibility.Visible;
        var painel = new StackPanel();
        if (previa.Partes.Count > 0)
            painel.Children.Add(Linha("Abas: " + string.Join(" · ", previa.Partes), "TextSecondaryBrush"));

        // Grade de verdade, coluna por coluna: separar as células por "│" num texto só deixava
        // as colunas tortas, cada linha com a sua largura (visto no ensaio de 19/10/2026).
        var grade = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        int colunas = previa.Linhas.Count == 0 ? 0 : previa.Linhas.Max(l => l.Count);
        for (int c = 0; c < colunas; c++)
            grade.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == colunas - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });

        bool planilha = previa.Partes.Count > 0;
        for (int l = 0; l < previa.Linhas.Count; l++)
        {
            grade.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < previa.Linhas[l].Count; c++)
            {
                var t = Texto(previa.Linhas[l][c], planilha && l == 0 ? "TextStrongBrush" : "TextBodyBrush", mono: true);
                t.Margin = new Thickness(0, 3, 14, 3);
                t.TextWrapping = colunas == 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                if (planilha && l == 0) t.FontWeight = FontWeights.SemiBold;
                Grid.SetRow(t, l);
                Grid.SetColumn(t, c);
                grade.Children.Add(t);
            }
        }
        painel.Children.Add(grade);

        if (previa.Omitidas > 0)
            painel.Children.Add(Linha($"+ {previa.Omitidas} linhas", "TextMutedBrush"));

        PlanoTabela.Children.Add(painel);
    }

    private TextBlock Linha(string texto, string cor, bool mono = false)
    {
        var t = Texto(texto, cor, mono);
        t.Margin = new Thickness(0, 1, 0, 1);
        return t;
    }

    private TextBlock Texto(string texto, string cor, bool mono)
    {
        var t = new TextBlock
        {
            Text = texto,
            FontSize = 11.5,
            Foreground = Pincel(cor),
            TextWrapping = TextWrapping.Wrap
        };
        if (mono) t.FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFontFamily");
        return t;
    }

    private Brush Pincel(string chave) => (Brush)FindResource(chave);
}
