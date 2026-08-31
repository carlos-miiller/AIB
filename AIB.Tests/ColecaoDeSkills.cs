using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Serializa as classes que mexem em <c>SkillService.SkillsDirectoryOverride</c>.
    /// <para>
    /// O override e estatico e o xUnit roda classes em PARALELO: sem esta colecao, uma classe
    /// aponta a raiz para a pasta dela, outra aponta para a sua, e as duas passam a enxergar
    /// as skills da vizinha. Foi exatamente o que aconteceu — dois ensaios verdes viraram
    /// vermelhos por verem uma habilidade que nunca instalaram.
    /// </para>
    /// <para>
    /// As classes que so LEEM as skills reais entram aqui tambem: o registry decide registrar
    /// a execute_skill pela contagem, entao ele precisa nao ver a raiz de outro ensaio.
    /// </para>
    /// </summary>
    [CollectionDefinition("Skills")]
    public class ColecaoDeSkills { }
}
