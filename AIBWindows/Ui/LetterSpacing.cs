using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AIB.Ui;

/// <summary>
/// Espaçamento entre letras para <see cref="TextBlock"/>.
/// <para>
/// As specs pedem <c>letter-spacing: 0.10em</c> nos rótulos em caixa alta (cabeçalho de seção,
/// título de aba do painel, separador de dia do log) e mandam usar
/// <c>TextBlock.CharacterSpacing="100"</c>. Essa propriedade é do WinUI: o
/// <see cref="TextBlock"/> do WPF não tem equivalente, nem nativo nem por Typography.
/// </para>
/// <para>
/// O jeito que funciona é intercalar um <see cref="Run"/> de espaço com fonte reduzida entre os
/// caracteres. Um espaço no Segoe UI mede cerca de 0,276 em, então para abrir uma folga de
/// <c>tracking × fontSize</c> pixels o espaço precisa de <c>tracking × fontSize / 0,276</c> de
/// corpo. A conta está em <see cref="LarguraDoEspaco"/> para ficar conferível.
/// </para>
/// <para>
/// Só serve para rótulo curto e estático. O texto passa a viver em Inlines, então quem usa isto
/// não pode também ligar Text a um binding que mude com frequência.
/// </para>
/// </summary>
public static class LetterSpacing
{
    /// <summary>Fração de em que um caractere de espaço ocupa no Segoe UI.</summary>
    private const double LarguraDoEspaco = 0.276;

    /// <summary>Espaçamento em em. 0.10 equivale ao <c>letter-spacing: 0.10em</c> das specs.</summary>
    public static readonly DependencyProperty EmProperty =
        DependencyProperty.RegisterAttached(
            "Em",
            typeof(double),
            typeof(LetterSpacing),
            new PropertyMetadata(0.0, AoMudar));

    public static void SetEm(DependencyObject alvo, double valor) => alvo.SetValue(EmProperty, valor);

    public static double GetEm(DependencyObject alvo) => (double)alvo.GetValue(EmProperty);

    private static void AoMudar(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock bloco) return;

        double em = (double)e.NewValue;
        if (em <= 0) return;

        // O texto pode ainda não ter sido atribuído quando a propriedade chega (ordem dos
        // atributos no XAML). Nesse caso espera o Loaded.
        if (string.IsNullOrEmpty(bloco.Text))
        {
            bloco.Loaded += AoCarregar;
            return;
        }

        Aplicar(bloco, em);
    }

    private static void AoCarregar(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock bloco) return;
        bloco.Loaded -= AoCarregar;
        Aplicar(bloco, GetEm(bloco));
    }

    private static void Aplicar(TextBlock bloco, double em)
    {
        string texto = bloco.Text;
        if (string.IsNullOrEmpty(texto) || em <= 0) return;

        double corpo = bloco.FontSize > 0 ? bloco.FontSize : 12.0;
        double corpoDoEspaco = em * corpo / LarguraDoEspaco;

        bloco.Inlines.Clear();

        for (int i = 0; i < texto.Length; i++)
        {
            bloco.Inlines.Add(new Run(texto[i].ToString()));

            // Sem folga depois do último caractere: ela deslocaria o alinhamento à direita.
            if (i < texto.Length - 1)
                bloco.Inlines.Add(new Run(" ") { FontSize = corpoDoEspaco });
        }
    }
}
