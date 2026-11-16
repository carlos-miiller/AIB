using System;
using System.Linq;
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
    /// <summary>
    /// A janela do orbe, quando está na tela — as flutuantes nascem DONAS dela. Ligado pelo App;
    /// null nos ensaios.
    /// </summary>
    public static Func<Window?>? Dono { get; set; }

    /// <summary>A fila do app. Trocável no ensaio, que não quer janelas de verdade na tela.</summary>
    public static FilaDeJanelas<Window> Fila { get; set; } =
        new(janela => Exibir(janela, Dono?.Invoke()), janela => janela.Hide());

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

    /// <summary>
    /// Fecha uma flutuante que perdeu o sentido — na tela ou ainda esperando a vez. Nula, ou
    /// já fechada, não faz nada.
    /// </summary>
    public static void Fechar(Window? janela) => Fechar(janela, Fila);

    public static void Fechar(Window? janela, FilaDeJanelas<Window> fila)
    {
        if (janela == null || !fila.Contem(janela)) return;

        // Saiu explícito: a que nunca apareceu não conta com o Closed.
        fila.Saiu(janela);
        janela.Close();
    }

    /// <summary>
    /// Mostra a janela pertencendo ao orbe.
    /// <para>
    /// As duas são Topmost, e entre duas Topmost fica em cima a última ativada. Visto no uso
    /// (19/10/2026): o Shadow pede confirmação, a janela dela toma o foco e a barra fecha;
    /// reaberta a barra, o orbe passava POR CIMA da confirmação, que ficava escondida atrás
    /// dele esperando resposta. Janela com dono fica sempre acima do dono, sem precisar roubar
    /// o foco de volta — o que fecharia a barra de novo.
    /// </para>
    /// </summary>
    public static void Exibir(Window janela, Window? dono)
    {
        if (dono != null && !ReferenceEquals(dono, janela) && dono.IsVisible && janela.Owner == null)
        {
            janela.Owner = dono;

            // O foco que vai do orbe para a pergunta não fecha a barra; o que sai da pergunta
            // para outro programa, sim. Ver ShadowAssistantWindow.ConferirFoco.
            if (dono is ShadowAssistantWindow orbe) janela.Deactivated += (_, _) => orbe.ConferirFoco();
        }
        janela.Show();
    }

    /// <summary>
    /// Solta as flutuantes do orbe antes de ele fechar. O WPF fecha as janelas de um dono que
    /// fecha, e desligar o orbe recusaria em silêncio a confirmação que estava na tela.
    /// </summary>
    public static void Soltar(Window dono)
    {
        foreach (var janela in dono.OwnedWindows.Cast<Window>().ToList())
            janela.Owner = null;
    }
}
