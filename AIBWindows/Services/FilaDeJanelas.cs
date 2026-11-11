using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services;

/// <summary>
/// A vez de cada janela flutuante na fila. Menor passa na frente.
/// <para>
/// PERGUNTA (confirmar ação, senha do terminal) vem antes de tudo e é a única que tira um aviso
/// da tela: há uma ferramenta parada esperando a resposta. Os avisos não se interrompem entre
/// si — a rajada só fura a FILA, não a janela que já está sendo lida.
/// </para>
/// </summary>
public enum VezDaJanela
{
    Pergunta = 0,
    Rajada = 1,
    RespostaSua = 2,
    Lembrete = 3,
    Pronta = 4,
    Triagem = 5
}

/// <summary>
/// Uma janela flutuante por vez — o resto espera.
/// <para>
/// Defeito visto pelo usuário (16/10/2026): quando o modelo pedia várias ferramentas na mesma
/// volta, cada uma abria a própria confirmação perto do orbe, e elas nasciam uma em cima da
/// outra — a de baixo escondida, com a ferramenta dela parada sem ninguém ver. O mesmo com
/// dois pedidos de senha do terminal, que abriam um diálogo modal dentro do outro.
/// </para>
/// <para>
/// Genérica e sem WPF: quem mostra e esconde são os dois delegados. Em produção são
/// <c>Window.Show</c> e <c>Window.Hide</c>; no ensaio, listas.
/// </para>
/// <para>
/// Só na thread de interface — não tem trava. Quem chama de fora entra pelo Dispatcher.
/// </para>
/// </summary>
public sealed class FilaDeJanelas<T> where T : class
{
    private readonly Action<T> _mostrar;
    private readonly Action<T> _esconder;
    private readonly List<(T Item, VezDaJanela Vez, long Ordem)> _esperando = new();
    private long _ordem;

    public FilaDeJanelas(Action<T> mostrar, Action<T> esconder)
    {
        _mostrar = mostrar ?? throw new ArgumentNullException(nameof(mostrar));
        _esconder = esconder ?? throw new ArgumentNullException(nameof(esconder));
    }

    /// <summary>A janela na tela. Nula quando não há nenhuma.</summary>
    public T? Atual { get; private set; }

    /// <summary>A vez da janela na tela.</summary>
    public VezDaJanela VezAtual { get; private set; }

    /// <summary>Quantas esperam — o "+N" da janela de cima.</summary>
    public int Esperando => _esperando.Count;

    /// <summary>A fila mudou: entrou, saiu ou trocou a de cima.</summary>
    public event Action? Mudou;

    /// <summary>
    /// Põe uma janela na fila. Sem nenhuma na tela, ela aparece já; com um AVISO na tela e uma
    /// PERGUNTA chegando, o aviso sai (volta para o começo da fila) e a pergunta entra.
    /// </summary>
    public void Entrar(T item, VezDaJanela vez)
    {
        if (item == null || Contem(item)) return;

        if (Atual == null)
        {
            Exibir(item, vez);
        }
        else if (vez == VezDaJanela.Pergunta && VezAtual != VezDaJanela.Pergunta)
        {
            // Ordem negativa: dentro da vez dele, o aviso interrompido é o primeiro a voltar.
            var interrompido = Atual;
            _esperando.Add((interrompido, VezAtual, -(++_ordem)));
            _esconder(interrompido);
            Exibir(item, vez);
        }
        else
        {
            _esperando.Add((item, vez, ++_ordem));
        }

        Mudou?.Invoke();
    }

    /// <summary>
    /// A janela fechou (respondida, dispensada ou descartada sem nunca ter aparecido). Se era a
    /// de cima, a próxima da fila aparece.
    /// </summary>
    public void Saiu(T item)
    {
        if (item == null) return;

        if (ReferenceEquals(item, Atual))
        {
            Atual = null;

            if (_esperando.Count > 0)
            {
                var proxima = _esperando.OrderBy(e => e.Vez).ThenBy(e => e.Ordem).First();
                _esperando.Remove(proxima);
                Exibir(proxima.Item, proxima.Vez);
            }
        }
        else
        {
            _esperando.RemoveAll(e => ReferenceEquals(e.Item, item));
        }

        Mudou?.Invoke();
    }

    public bool Contem(T item) =>
        ReferenceEquals(item, Atual) || _esperando.Any(e => ReferenceEquals(e.Item, item));

    private void Exibir(T item, VezDaJanela vez)
    {
        Atual = item;
        VezAtual = vez;
        _mostrar(item);
    }
}
