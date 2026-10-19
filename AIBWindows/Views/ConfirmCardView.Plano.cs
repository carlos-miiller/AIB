using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly HashSet<int> _gruposExpandidos = new();
    private readonly Dictionary<int, List<UIElement>> _extrasPorGrupo = new();
    private readonly Dictionary<int, UIElement> _linkMostrarPorGrupo = new();
    private readonly Dictionary<int, UIElement> _linkMenosPorGrupo = new();

    public bool Expandido(int grupo) => _gruposExpandidos.Contains(grupo);

    public void AlternarGrupo(int grupo)
    {
        if (_gruposExpandidos.Contains(grupo))
        {
            _gruposExpandidos.Remove(grupo);
            if (_linkMostrarPorGrupo.TryGetValue(grupo, out var linkMostrar))
                linkMostrar.Visibility = Visibility.Visible;
            if (_extrasPorGrupo.TryGetValue(grupo, out var extras))
            {
                foreach (var el in extras)
                    el.Visibility = Visibility.Collapsed;
            }
            if (_linkMenosPorGrupo.TryGetValue(grupo, out var linkMenos))
                linkMenos.Visibility = Visibility.Collapsed;
        }
        else
        {
            _gruposExpandidos.Add(grupo);
            if (_linkMostrarPorGrupo.TryGetValue(grupo, out var linkMostrar))
                linkMostrar.Visibility = Visibility.Collapsed;
            if (_extrasPorGrupo.TryGetValue(grupo, out var extras))
            {
                foreach (var el in extras)
                    el.Visibility = Visibility.Visible;
            }
            if (_linkMenosPorGrupo.TryGetValue(grupo, out var linkMenos))
                linkMenos.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Desenha o plano. Chamado pelo <see cref="Preencher"/> quando há um.</summary>
    private void MostrarPlano(PlanoDeGravacao plano)
    {
        _gruposExpandidos.Clear();
        _extrasPorGrupo.Clear();
        _linkMostrarPorGrupo.Clear();
        _linkMenosPorGrupo.Clear();

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
            PlanoEfeitosScroll.Visibility = Visibility.Visible;
            PlanoEfeitos.Visibility = Visibility.Visible;
            // Só aparência não mexe em conta: ali as linhas são as regras condicionais que ficam.
            if (!plano.SoAparencia)
                PlanoEfeitos.Children.Add(Linha("Efeito nas fórmulas que já existem:", "TextSecondaryBrush"));
            foreach (var e in plano.Efeitos) PlanoEfeitos.Children.Add(Linha(e, "AccentBlueBrush", mono: true));
        }

        if (plano.Avisos.Count > 0)
        {
            PlanoAvisosScroll.Visibility = Visibility.Visible;
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

        int grupoAtual = 0;

        List<UIElement> ObterExtras(int gId)
        {
            if (!_extrasPorGrupo.TryGetValue(gId, out var lista))
            {
                lista = new List<UIElement>();
                _extrasPorGrupo[gId] = lista;
            }
            return lista;
        }

        void FecharGrupoSeAberto()
        {
            if (grupoAtual > 0)
            {
                int gId = grupoAtual;
                linha++;
                Nova();
                var b = CriarLink("mostrar menos", gId, centro: true);
                b.Visibility = Visibility.Collapsed;
                Grid.SetRow(b, linha);
                Grid.SetColumnSpan(b, 3);
                g.Children.Add(b);
                _linkMenosPorGrupo[gId] = b;
                grupoAtual = 0;
            }
        }

        foreach (var m in plano.Mudancas)
        {
            if (!m.Extra && !(m.Tipo == TipoDeMudanca.Mais && m.Grupo > 0) && grupoAtual > 0)
            {
                FecharGrupoSeAberto();
            }

            linha++;
            Nova();

            switch (m.Tipo)
            {
                case TipoDeMudanca.Grupo:
                    FecharGrupoSeAberto();
                    Inteira(m.Onde, "TextStrongBrush", FontWeights.SemiBold, null);
                    break;

                case TipoDeMudanca.Mais when m.Grupo > 0:
                    FecharGrupoSeAberto();
                    grupoAtual = m.Grupo;
                    var linkMostrar = CriarLink($"{m.Onde} · mostrar", m.Grupo, centro: true);
                    Grid.SetRow(linkMostrar, linha);
                    Grid.SetColumnSpan(linkMostrar, 3);
                    g.Children.Add(linkMostrar);
                    _linkMostrarPorGrupo[m.Grupo] = linkMostrar;
                    break;

                case TipoDeMudanca.Mais:
                    var bMais = Inteira(m.Onde, "TextMutedBrush", FontWeights.Normal, null, centro: true);
                    if (m.Extra)
                    {
                        bMais.Visibility = Visibility.Collapsed;
                        ObterExtras(m.Grupo > 0 ? m.Grupo : grupoAtual).Add(bMais);
                    }
                    break;

                case TipoDeMudanca.Sai:
                    var bSai = Inteira("− " + m.Onde + "   " + m.Antes, "DangerTextBrush", FontWeights.Normal, "DangerFill12Brush", mono: true);
                    if (m.Extra)
                    {
                        bSai.Visibility = Visibility.Collapsed;
                        ObterExtras(m.Grupo > 0 ? m.Grupo : grupoAtual).Add(bSai);
                    }
                    break;

                case TipoDeMudanca.Entra:
                    var bEntra = Inteira("+ " + m.Onde + "   " + m.Depois, "SuccessBrush", FontWeights.Normal, "SuccessFill10Brush", mono: true);
                    if (m.Extra)
                    {
                        bEntra.Visibility = Visibility.Collapsed;
                        ObterExtras(m.Grupo > 0 ? m.Grupo : grupoAtual).Add(bEntra);
                    }
                    break;

                default:
                    UIElement t0 = Celula(m.Onde, 0, "TextSecondaryBrush");
                    UIElement t1 = m.AmostraAntes is { } aa
                        ? Amostrada(aa, m.Antes ?? "", 1, "DangerTextBrush")
                        : Celula(m.Antes ?? "vazia", 1, m.Antes == null ? "TextMutedBrush" : "DangerTextBrush", italico: m.Antes == null);
                    UIElement t2 = m.AmostraDepois is { } ad
                        ? Amostrada(ad, m.Depois ?? "", 2, "SuccessBrush")
                        : Celula(m.Depois ?? "vazia", 2, m.Depois == null ? "TextMutedBrush" : m.Erro ? "DangerTextBrush" : "SuccessBrush",
                                 italico: m.Depois == null);
                    if (m.Extra)
                    {
                        t0.Visibility = Visibility.Collapsed;
                        t1.Visibility = Visibility.Collapsed;
                        t2.Visibility = Visibility.Collapsed;
                        var extras = ObterExtras(m.Grupo > 0 ? m.Grupo : grupoAtual);
                        extras.Add(t0);
                        extras.Add(t1);
                        extras.Add(t2);
                    }
                    break;
            }
        }

        FecharGrupoSeAberto();

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

        TextBlock Celula(string texto, int col, string cor, bool italico = false)
        {
            var t = Texto(texto, cor, mono: true);
            t.Margin = new Thickness(0, 4, 12, 4);
            if (italico) t.FontStyle = FontStyles.Italic;
            Grid.SetRow(t, linha);
            Grid.SetColumn(t, col);
            g.Children.Add(t);
            return t;
        }

        // A amostra e, embaixo, a descrição em palavras (mockup "Formatação de planilhas do AIB").
        StackPanel Amostrada(Aparencia aparencia, string descricao, int col, string cor)
        {
            var p = new StackPanel { Margin = new Thickness(0, 4, 12, 4) };
            p.Children.Add(Amostra(aparencia));
            if (descricao.Length > 0)
            {
                var d = Texto(descricao, cor, mono: false);
                d.FontSize = 11;
                d.Margin = new Thickness(0, 4, 0, 0);
                p.Children.Add(d);
            }
            Grid.SetRow(p, linha);
            Grid.SetColumn(p, col);
            g.Children.Add(p);
            return p;
        }

        Border Inteira(string texto, string cor, FontWeight peso, string? fundo, bool centro = false, bool mono = false)
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
            return b;
        }

        Border CriarLink(string texto, int gId, bool centro = true)
        {
            var t = Texto(texto, "AccentLilacBrush", mono: false);
            t.FontWeight = FontWeights.Normal;
            t.TextAlignment = centro ? TextAlignment.Center : TextAlignment.Left;
            t.Cursor = System.Windows.Input.Cursors.Hand;
            var b = new Border
            {
                Child = t,
                Padding = new Thickness(4, 4, 4, 4),
                Margin = new Thickness(-4, 0, -4, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            b.MouseEnter += (s, e) => t.TextDecorations = TextDecorations.Underline;
            b.MouseLeave += (s, e) => t.TextDecorations = null;
            b.MouseLeftButtonUp += (s, e) => AlternarGrupo(gId);
            return b;
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

    /// <summary>
    /// A célula como o Excel a mostra: fundo branco e grade cinza (os tokens Celula*, iguais nos
    /// dois temas), e por cima as cores que vêm do arquivo — essas são dado, não tema.
    /// </summary>
    internal Border Amostra(Aparencia a)
    {
        var t = new TextBlock
        {
            Text = a.Texto.Length == 0 ? " " : a.Texto,
            FontFamily = new System.Windows.Media.FontFamily("Calibri, Segoe UI"),
            FontSize = 12,
            Foreground = CorDoArquivo(a.Cor) ?? Pincel("CelulaTextoBrush"),
            FontWeight = a.Negrito ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = a.Italico ? FontStyles.Italic : FontStyles.Normal,
            TextDecorations = a.Sublinhado ? TextDecorations.Underline : null,
            TextAlignment = a.Alinhamento switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left },
            TextTrimming = TextTrimming.CharacterEllipsis,
            Padding = new Thickness(6, 2, 6, 2)
        };

        Brush? fundo = CorDoArquivo(a.Fundo);
        if (a.Escala is { Count: > 0 } escala)
        {
            var gradiente = new System.Windows.Media.LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0.5), EndPoint = new System.Windows.Point(1, 0.5) };
            for (int i = 0; i < escala.Count; i++)
                if (Cor(escala[i]) is { } c)
                    gradiente.GradientStops.Add(new System.Windows.Media.GradientStop(c, escala.Count == 1 ? 0 : (double)i / (escala.Count - 1)));
            fundo = gradiente;
        }

        double grossa = 2;
        var b = a.Bordas;
        var dentro = new Border
        {
            Child = t,
            Background = fundo ?? Pincel("CelulaFundoBrush"),
            BorderBrush = Pincel("CelulaTextoBrush"),
            BorderThickness = new Thickness(
                b.HasFlag(Bordas.Esquerda) ? grossa : 0,
                b.HasFlag(Bordas.Cima) ? grossa : 0,
                b.HasFlag(Bordas.Direita) ? grossa : 0,
                b.HasFlag(Bordas.BaixoDupla) ? 3 : b.HasFlag(Bordas.Baixo) ? grossa : 0)
        };

        return new Border
        {
            Child = dentro,
            BorderBrush = Pincel("CelulaGradeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            MinWidth = 92,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ToolTip = a.Texto
        };
    }

    private static System.Windows.Media.Color? Cor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex); }
        catch (System.FormatException) { return null; }
    }

    private static Brush? CorDoArquivo(string? hex) =>
        Cor(hex) is { } c ? new System.Windows.Media.SolidColorBrush(c) : null;

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
