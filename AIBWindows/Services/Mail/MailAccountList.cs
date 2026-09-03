using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AIB.Services.Mail;

/// <summary>Por que uma mutação da lista foi recusada. Vazio = aceita.</summary>
public readonly record struct ResultadoDaLista(bool Ok, string Erro)
{
    public static ResultadoDaLista Aceito => new(true, "");
    public static ResultadoDaLista Recusado(string erro) => new(false, erro);
}

/// <summary>
/// A coleção de caixas com as invariantes de §7 A15 e A16 aplicadas em TODA mutação.
/// <para>
/// As invariantes não moram na tela de propósito. A A15 diz que adicionar, remover e promover
/// agem na hora, fora do "Salvar" do rodapé — ou seja, são três caminhos distintos que podem
/// quebrar as mesmas regras. Escritas na View, seriam três cópias; aqui são uma, e têm ensaio.
/// </para>
/// </summary>
public sealed class MailAccountList
{
    private readonly ObservableCollection<MailAccount> _contas = new();

    public ObservableCollection<MailAccount> Contas => _contas;

    public int Count => _contas.Count;

    /// <summary>
    /// Há pelo menos uma caixa com senha no cofre. É o que liga a aba de e-mails da janela de
    /// chat (tela-chat-v3 §6.2.1).
    /// </summary>
    public bool Configurado => _contas.Any(c => c.HasPassword);

    public MailAccount? Principal => _contas.FirstOrDefault(c => c.IsPrimary);

    public bool Contem(string endereco) =>
        _contas.Any(c => string.Equals(c.Address, (endereco ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Acrescenta uma caixa. A PRIMEIRA nasce principal — sem isso a lista ficaria sem
    /// principal nenhuma até alguém clicar na estrela, e a invariante "com a lista não vazia,
    /// exatamente uma" começaria violada.
    /// </summary>
    public ResultadoDaLista Adicionar(MailAccount conta)
    {
        if (conta == null) return ResultadoDaLista.Recusado("conta nula");

        conta.Address = (conta.Address ?? "").Trim();

        if (!ImapHostGuesser.EnderecoParecevalido(conta.Address))
            return ResultadoDaLista.Recusado("endereço de e-mail inválido");

        if (Contem(conta.Address))
            return ResultadoDaLista.Recusado("esta conta já está na lista");

        conta.IsPrimary = _contas.Count == 0;
        _contas.Add(conta);
        return ResultadoDaLista.Aceito;
    }

    /// <summary>
    /// Remove uma caixa.
    /// <para>
    /// A principal com outras contas na lista NÃO sai — §7 A15. O motivo não é burocracia: a
    /// principal é de onde sairia qualquer envio, e uma lista com três contas e nenhuma
    /// principal é um estado que nada na tela mostra e que só apareceria na hora de enviar.
    /// Quem quer remover a principal promove outra primeiro, e o ToolTip do botão diz isso.
    /// </para>
    /// <para>
    /// A ÚLTIMA conta sai mesmo sendo principal: aí a lista fica vazia, que é um estado
    /// desenhado (§3.12, estado vazio).
    /// </para>
    /// </summary>
    public ResultadoDaLista Remover(MailAccount conta)
    {
        if (conta == null || !_contas.Contains(conta))
            return ResultadoDaLista.Recusado("conta não está na lista");

        if (conta.IsPrimary && _contas.Count > 1)
            return ResultadoDaLista.Recusado("promova outra conta a principal antes de remover esta");

        _contas.Remove(conta);
        return ResultadoDaLista.Aceito;
    }

    /// <summary>Se a remoção passaria. É o CanExecute do botão de lixeira.</summary>
    public bool PodeRemover(MailAccount conta) =>
        conta != null && _contas.Contains(conta) && (!conta.IsPrimary || _contas.Count == 1);

    /// <summary>Promove uma caixa. Exatamente uma principal, sempre.</summary>
    public ResultadoDaLista TornarPrincipal(MailAccount conta)
    {
        if (conta == null || !_contas.Contains(conta))
            return ResultadoDaLista.Recusado("conta não está na lista");

        foreach (var c in _contas) c.IsPrimary = ReferenceEquals(c, conta);
        return ResultadoDaLista.Aceito;
    }

    /// <summary>
    /// Substitui o conteúdo pelo que veio do disco, aplicando as invariantes.
    /// <para>
    /// Um arquivo de configuração editado à mão pode chegar com duas principais, com nenhuma
    /// ou com endereços repetidos. Confiar nele e só validar na interface deixaria o estado
    /// inválido vivo até a próxima mutação.
    /// </para>
    /// </summary>
    public void Repovoar(IEnumerable<MailAccount> vindas)
    {
        _contas.Clear();

        foreach (var conta in vindas ?? Enumerable.Empty<MailAccount>())
        {
            conta.Address = (conta.Address ?? "").Trim();
            if (!ImapHostGuesser.EnderecoParecevalido(conta.Address)) continue;
            if (Contem(conta.Address)) continue;

            _contas.Add(conta);
        }

        // Zero ou várias principais viram exatamente uma, a primeira da lista.
        var principais = _contas.Where(c => c.IsPrimary).ToList();
        if (_contas.Count > 0 && principais.Count != 1)
            TornarPrincipal(_contas[0]);
    }
}
