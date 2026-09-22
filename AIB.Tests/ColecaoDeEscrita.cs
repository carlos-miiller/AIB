using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Serializa as classes que mexem em <c>PastasSemConfirmacao.Configurar</c>.
    /// <para>
    /// A dispensa e ESTATICA — proposital: as ferramentas nao carregam settings, e um unico
    /// ponto de sincronia no SettingsService e o que impede o caso "mudei nas configuracoes e a
    /// ferramenta continuou com a lista velha". O preco e que o xUnit roda classes em PARALELO:
    /// uma dispensa ligada por uma classe faria outra executar sem nunca perguntar.
    /// </para>
    /// <para>
    /// Mesma licao do ColecaoDeSkills, e a segunda vez que ela aparece: estado estatico e teste
    /// paralelo so convivem com a colecao declarada.
    /// </para>
    /// <para>
    /// E POR ISSO SEM PARALELISMO COM AS OUTRAS COLECOES. Declarar a colecao serializa as classes
    /// que chamam Configurar de proposito, mas nao as que chamam SEM SABER: todo
    /// <c>SettingsService.LoadSettings</c> reconfigura a lista, e qualquer classe solta que leia
    /// configuracoes derrubava a dispensa no meio de um ensaio daqui. Aparecia como falha
    /// intermitente de "o portao nao devia ter perguntado", uma rodada em cada tres.
    /// </para>
    /// </summary>
    [CollectionDefinition("Escrita", DisableParallelization = true)]
    public class ColecaoDeEscrita { }
}
