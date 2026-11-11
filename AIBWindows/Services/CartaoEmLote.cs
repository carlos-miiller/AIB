using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AIB.Services;

/// <summary>
/// O lugar de UMA chamada num cartão dividido com outras do mesmo lote paralelo. Visto no uso
/// (21/10/2026): a Ellen pediu 27 vezes a habilidade bitrix no mesmo turno, uma por tarefa, e
/// vieram 27 cartões. Agora as chamadas que a ferramenta junta (<see cref="ITool.ChaveDoCartao"/>)
/// esperam umas pelas outras e fazem uma pergunta só.
/// <para>
/// Cada chamada ou PERGUNTA (<see cref="PerguntarAsync"/>) ou SAI (<see cref="Dispose"/>): a que
/// foi recusada no pré-voo, barrada pelo piso ou liberada pelo "sempre" não chega ao cartão. O
/// cartão só sai quando todas responderam por uma das duas portas — daí o <c>using</c> em quem
/// recebe a vaga, para que nenhum caminho de saída deixe as outras esperando para sempre.
/// </para>
/// </summary>
public sealed class VagaNoCartao : IDisposable
{
    private readonly GrupoDoCartao _grupo;
    private readonly int _indice;
    private bool _usada;

    internal VagaNoCartao(GrupoDoCartao grupo, int indice)
    {
        _grupo = grupo;
        _indice = indice;
    }

    /// <summary>
    /// Entra no cartão com o contexto desta chamada e espera a resposta, que é a mesma para todas.
    /// </summary>
    public Task<RespostaDoPortao> PerguntarAsync(CommandConfirmationContext contexto)
    {
        if (_usada) throw new InvalidOperationException("Esta vaga já perguntou ou saiu.");
        _usada = true;
        return _grupo.Entrar(_indice, contexto);
    }

    /// <summary>Sai sem perguntar. Depois de perguntar, não faz nada.</summary>
    public void Dispose()
    {
        if (_usada) return;
        _usada = true;
        _grupo.Sair();
    }
}

/// <summary>As chamadas de um lote que dividem o cartão. Ver <see cref="VagaNoCartao"/>.</summary>
internal sealed class GrupoDoCartao
{
    private readonly ITool _ferramenta;
    private readonly Func<CommandConfirmationContext, Task<RespostaDoPortao>> _perguntar;
    private readonly List<(int Indice, CommandConfirmationContext Contexto)> _pedidos = new();

    // A resposta do cartão, a ordem em que as chamadas apareceram nele e as posições recusadas
    // uma a uma (cartão com decisão por item, como o do doc).
    private readonly TaskCompletionSource<(RespostaDoPortao Resposta, List<int> Ordem, ISet<int> Recusados)> _resposta =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _faltam;

    public GrupoDoCartao(ITool ferramenta, int tamanho, Func<CommandConfirmationContext, Task<RespostaDoPortao>> perguntar)
    {
        _ferramenta = ferramenta;
        _faltam = tamanho;
        _perguntar = perguntar;
    }

    public async Task<RespostaDoPortao> Entrar(int indice, CommandConfirmationContext contexto)
    {
        bool ultima;
        lock (_pedidos)
        {
            _pedidos.Add((indice, contexto));
            ultima = --_faltam == 0;
        }
        if (ultima) _ = PerguntarAsync();

        // Permitido no cartão, mas esta chamada foi recusada no botão dela: só ela não roda.
        var (resposta, ordem, recusados) = await _resposta.Task.ConfigureAwait(false);
        return resposta.Allowed && recusados.Contains(ordem.IndexOf(indice))
            ? new RespostaDoPortao(false, false, null)
            : resposta;
    }

    public void Sair()
    {
        bool ultima;
        lock (_pedidos) ultima = --_faltam == 0 && _pedidos.Count > 0;
        if (ultima) _ = PerguntarAsync();
    }

    private async Task PerguntarAsync()
    {
        try
        {
            // Na ordem das chamadas, e não na de chegada: o cartão lista o que o modelo pediu.
            var ordenados = _pedidos.OrderBy(p => p.Indice).ToList();
            var contextos = ordenados.Select(p => p.Contexto).ToList();
            var cartao = contextos.Count == 1 ? contextos[0] : _ferramenta.JuntarNoCartao(contextos);
            var resposta = await _perguntar(cartao).ConfigureAwait(false);
            _resposta.TrySetResult((resposta, ordenados.Select(p => p.Indice).ToList(), cartao.Recusados));
        }
        catch (Exception ex)
        {
            _resposta.TrySetException(ex);
        }
    }
}
