using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Serializa as classes que mexem em <c>PastasPermitidas.Configurar</c>.
    /// <para>
    /// O confinamento e ESTATICO — proposital: as ferramentas nao carregam settings, e um unico
    /// ponto de sincronia no SettingsService e o que impede o caso "mudei nas configuracoes e a
    /// ferramenta continuou com a lista velha". O preco e que o xUnit roda classes em PARALELO:
    /// um confinamento ligado por uma classe reprovaria a gravacao legitima de outra.
    /// </para>
    /// <para>
    /// Mesma licao do ColecaoDeSkills, e a segunda vez que ela aparece: estado estatico e teste
    /// paralelo so convivem com a colecao declarada.
    /// </para>
    /// </summary>
    [CollectionDefinition("Escrita")]
    public class ColecaoDeEscrita { }
}
