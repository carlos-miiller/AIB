using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Serializa as classes que gravam no historico de conversas.
    /// <para>
    /// O arquivo e UM SO para a suite inteira — o ModuleInitializer aponta
    /// <c>HistoryDirectoryOverride</c> para uma pasta temporaria, mas so uma. E
    /// <c>SaveCurrentSession</c> faz ler-modificar-gravar sem trava: duas classes escrevendo ao
    /// mesmo tempo leem a mesma lista, cada uma acrescenta a sua entrada, e a segunda gravacao
    /// apaga a primeira. Lost update classico.
    /// </para>
    /// <para>
    /// O sintoma era um ensaio DIFERENTE falhando a cada execucao — ora o upsert nao achava o
    /// que acabara de gravar, ora outra classe perdia a dela. Verde tres vezes seguidas nao
    /// provava nada, porque a corrida depende de quem chega primeiro.
    /// </para>
    /// <para>
    /// Ha ainda o <c>Take(50)</c>: o historico guarda cinquenta sessoes. Com as classes em
    /// paralelo, as entradas de uma expulsavam as da outra antes de ela conferir.
    /// </para>
    /// <para>
    /// Terceira vez que esta licao aparece, depois do ColecaoDeSkills e do ColecaoDeEscrita:
    /// estado compartilhado e teste paralelo so convivem com a colecao declarada.
    /// </para>
    /// </summary>
    [CollectionDefinition("Historico")]
    public class ColecaoDeHistorico { }
}
