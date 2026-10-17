using System;
using System.Windows;
using AIB.Services;

namespace AIB.Views;

/// <summary>
/// A fila das janelas que aparecem fora da janela principal — uma por vez.
/// <para>
/// Toda janela flutuante entra por <see cref="Mostrar"/> em vez de chamar <c>Show</c>: é o que
/// impede duas confirmações de nascerem uma em cima da outra (ver <see cref="FilaDeJanelas{T}"/>).
/// </para>
/// </summary>
public static class Flutuantes
{
    /// <summary>A fila do app. Trocável no ensaio, que não quer janelas de verdade na tela.</summary>
    public static FilaDeJanelas<Window> Fila { get; set; } =
        new(janela => janela.Show(), janela => janela.Hide());

    /// <summary>
    /// Põe a janela na fila. Ela sai da fila sozinha ao fechar — inclusive fechada sem nunca ter
    /// aparecido, como a confirmação descartada enquanto esperava a vez.
    /// </summary>
    public static void Mostrar(Window janela, VezDaJanela vez)
    {
        if (janela == null) return;

        var fila = Fila;
        janela.Closed += (_, _) => fila.Saiu(janela);
        fila.Entrar(janela, vez);
    }
}
