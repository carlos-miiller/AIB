using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Uma identidade só no prompt. Com persona, a alma diz "Você é Kai" e as regras não podem
    /// dizer "Você é o AIB" logo depois — medido em 14/09: com as duas, o modelo respondia
    /// "quem é você?" sem dizer o próprio nome.
    /// </summary>
    public class IdentidadeDoPromptTests
    {
        [Fact]
        public void ComPersona_ASRegras_NAO_DeclaramOutraIdentidade()
        {
            string prompt = ConversationService.PromptBase(comPersona: true);

            prompt.Should().NotContain("Você é o AIB");
            prompt.Should().StartWith("Você opera dentro do AIB, agente local de IA no Windows do usuário.");
        }

        [Fact]
        public void SemPersona_OAIB_EhQuemFala()
        {
            ConversationService.PromptBase(comPersona: false)
                .Should().StartWith("Você é o AIB, agente local de IA no Windows do usuário.");
        }

        [Fact]
        public void ATroca_SO_MudaAPrimeiraLinha()
        {
            // Se a constante e a linha do prompt se desencontrarem, o Replace não acha nada e as
            // duas versões saem iguais — em silêncio. E se a troca pegar demais, o resto das
            // regras muda junto.
            string sem = ConversationService.PromptBase(comPersona: false);
            string com = ConversationService.PromptBase(comPersona: true);

            com.Should().NotBe(sem);
            com.Substring(com.IndexOf('\n')).Should().Be(sem.Substring(sem.IndexOf('\n')));
        }
    }
}
