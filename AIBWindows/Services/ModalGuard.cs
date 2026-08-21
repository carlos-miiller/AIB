using System;
using System.Threading;

namespace AIB.Services;

/// <summary>
/// Marca que existe um diálogo modal do próprio AIB na tela.
/// <para>
/// A ChatWindow se esconde ao perder o foco (o "ghosting" documentado em 02_Views_and_UI.md):
/// clicar num jogo ou noutro programa some com o chat. Só que abrir um modal também tira o foco
/// dela — então o chat desaparecia atrás do próprio diálogo que tinha acabado de abrir, e o
/// usuário precisava do atalho global para trazê-lo de volta.
/// </para>
/// <para>
/// A SettingsWindow contornava isso desinscrevendo o handler antes do ShowDialog e reinscrevendo
/// depois. Esse padrão precisa ser lembrado em cada novo modal — e já foi esquecido duas vezes
/// (o MessageBox do Shadow Assistant e o modal de confirmação de comando). Aqui a regra fica
/// num lugar só, e quem abre modal apenas declara que abriu.
/// </para>
/// </summary>
public static class ModalGuard
{
    private static int _depth;

    /// <summary>Há pelo menos um modal do AIB aberto agora.</summary>
    public static bool IsAnyModalOpen => Volatile.Read(ref _depth) > 0;

    /// <summary>
    /// Abre um escopo de modal. Contado, e não booleano, porque um modal pode abrir outro:
    /// um único flag seria zerado pelo primeiro que fechasse, e o ghosting voltaria a disparar
    /// com o segundo ainda na tela.
    /// </summary>
    public static IDisposable Enter()
    {
        Interlocked.Increment(ref _depth);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Guarda contra Dispose duplo: decrementar duas vezes deixaria o contador
            // negativo e desligaria o ghosting para o resto da sessão.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Decrement(ref _depth);
        }
    }
}
