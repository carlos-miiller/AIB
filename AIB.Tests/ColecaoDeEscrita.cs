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
    /// </summary>
    [CollectionDefinition("Escrita")]
    public class ColecaoDeEscrita { }
}
