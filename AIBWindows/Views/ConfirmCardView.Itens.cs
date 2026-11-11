using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;

using Button = System.Windows.Controls.Button;

namespace AIB.Views;

/// <summary>
/// Várias gravações de documento num cartão só, cada arquivo com o seu Permitir e Recusar —
/// mockup "Edição de documentos do AIB", cena "Várias mudanças de uma vez" (11/11/2026). Pedido do
/// usuário: a Ellen editava a mesma planilha em várias chamadas seguidas, e cada uma abria o seu
/// cartão (8 num dia na planilha de comparação).
/// <para>
/// O botão de baixo confirma o que ficou marcado: arquivo sem marca vai junto, e "Recusar tudo"
/// recusa todos. As posições recusadas vão para <see cref="CommandConfirmationContext.Recusados"/>
/// antes da resposta, e o grupo do cartão recusa só aquelas chamadas.
/// </para>
/// </summary>
public partial class ConfirmCardView
{
    // A marca de cada arquivo: null sem marca, true permitido, false recusado.
    private bool?[]? _marcas;
    private CommandConfirmationContext? _juntado;
    private readonly List<(Border Bloco, UIElement Corpo, TextBlock Nome, Button Sim, Button Nao)> _blocos = new();

    /// <summary>As marcas por arquivo, na ordem do cartão. Vazio fora do cartão de várias gravações.</summary>
    public IReadOnlyList<bool?> Marcas => _marcas ?? Array.Empty<bool?>();

    private void MostrarItens(CommandConfirmationContext juntado, IReadOnlyList<CommandConfirmationContext> itens)
    {
        _juntado = juntado;
        _marcas = new bool?[itens.Count];

        TituloText.Text = $"Gravar em {itens.Count} documentos?";
        ConsequenciaText.Text = "Cada arquivo tem o seu Permitir e Recusar; o botão de baixo confirma o que ficou "
                                + "marcado, e arquivo sem marca vai junto. Em cada arquivo é tudo ou nada: se uma "
                                + "mudança falhar, aquele arquivo fica como estava.";

        // Cada bloco mostra o seu caminho; a lista de comandos fica para o histórico e a auditoria.
        AlvoBorder.Visibility = Visibility.Collapsed;
        ItensPanel.Visibility = Visibility.Visible;

        for (int i = 0; i < itens.Count; i++)
        {
            var item = itens[i];
            string comando = item.Command ?? "";
            int espaco = comando.IndexOf(' ');
            string verbo = espaco > 0 ? comando[..espaco] : "";
            string caminho = espaco > 0 ? comando[(espaco + 1)..] : comando;

            var topo = new Grid();
            topo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rotulo = new TextBlock
            {
                Text = verbo, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Foreground = Pincel("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var nome = Texto(caminho, "TextStrongBrush", mono: true);
            nome.TextWrapping = TextWrapping.NoWrap;
            nome.TextTrimming = TextTrimming.CharacterEllipsis;
            nome.VerticalAlignment = VerticalAlignment.Center;
            nome.ToolTip = caminho;
            Grid.SetColumn(nome, 1);

            int posicao = i;
            var sim = Mini("Permitir", () => Marcar(posicao, true));
            var nao = Mini("Recusar", () => Marcar(posicao, false));
            Grid.SetColumn(sim, 2);
            Grid.SetColumn(nao, 3);
            foreach (UIElement e in new UIElement[] { rotulo, nome, sim, nao }) topo.Children.Add(e);

            UIElement corpo;
            if (item.Plano is { } plano)
            {
                var painel = NovoPainel();
                MostrarPlano(plano, painel);
                corpo = painel.Painel;
            }
            else
            {
                var previa = Texto(item.ScriptBody ?? "", "TextBodyBrush", mono: true);
                previa.Margin = new Thickness(0, 6, 0, 0);
                corpo = previa;
            }

            var pilha = new StackPanel();
            pilha.Children.Add(topo);
            pilha.Children.Add(corpo);

            var bloco = new Border
            {
                BorderBrush = Pincel("BorderCardBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 10),
                Margin = new Thickness(0, 10, 0, 0), Child = pilha
            };
            ItensPanel.Children.Add(bloco);
            _blocos.Add((bloco, corpo, nome, sim, nao));
        }

        RecusarButton.Content = "Recusar tudo";
        Atualizar();
    }

    private static Button Mini(string texto, Action aoClicar)
    {
        var b = new Button
        {
            Content = texto, FontSize = 11, Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(6, 0, 0, 0), MinWidth = 0, MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        b.SetResourceReference(StyleProperty, "OutlineButton");
        b.Click += (_, _) => aoClicar();
        return b;
    }

    /// <summary>
    /// Marca um arquivo como permitido (<paramref name="permitir"/>) ou recusado. Clicar de novo na
    /// mesma marca a tira. Depois de decidido o cartão, não muda nada.
    /// </summary>
    public void Marcar(int posicao, bool permitir)
    {
        if (_marcas == null || posicao < 0 || posicao >= _marcas.Length || !PermitirButton.IsEnabled) return;
        _marcas[posicao] = _marcas[posicao] == permitir ? null : permitir;
        Atualizar();
    }

    private void Atualizar()
    {
        if (_marcas == null) return;

        for (int i = 0; i < _blocos.Count; i++)
        {
            var (_, corpo, nome, sim, nao) = _blocos[i];
            Pintar(sim, _marcas[i] == true, "SuccessFill10Brush", "SuccessBrush", "SuccessEdge30Brush");
            Pintar(nao, _marcas[i] == false, "DangerFill12Brush", "DangerTextBrush", "DangerEdge32Brush");

            bool recusado = _marcas[i] == false;
            corpo.Opacity = recusado ? 0.4 : 1;
            nome.TextDecorations = recusado ? TextDecorations.Strikethrough : null;
            nome.Foreground = Pincel(recusado ? "TextSecondaryBrush" : "TextStrongBrush");
        }

        int total = _marcas.Length;
        int ficam = _marcas.Count(m => m != false);
        PermitirButton.Content = ficam == 0 ? "Confirmar a recusa"
            : ficam == total ? $"Permitir os {total}"
            : $"Permitir {ficam} de {total}";
    }

    private void Pintar(Button b, bool marcado, string fundo, string texto, string borda)
    {
        if (marcado)
        {
            b.Background = Pincel(fundo);
            b.Foreground = Pincel(texto);
            b.BorderBrush = Pincel(borda);
        }
        else
        {
            b.ClearValue(BackgroundProperty);
            b.ClearValue(ForegroundProperty);
            b.ClearValue(BorderBrushProperty);
        }
    }

    /// <summary>
    /// Na hora de responder: as posições recusadas vão para o contexto juntado. Devolve se algum
    /// arquivo segue permitido. Fora do cartão de várias gravações, devolve o que veio.
    /// </summary>
    private bool FecharItens(bool permitido)
    {
        if (_marcas == null || _juntado == null) return permitido;
        if (!permitido) return false;

        for (int i = 0; i < _marcas.Length; i++)
            if (_marcas[i] == false) _juntado.Recusados.Add(i);

        return _juntado.Recusados.Count < _marcas.Length;
    }
}
