using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A limpeza do título que o modelo devolve.
    /// <para>
    /// O prompt pede "só o título, sem aspas, sem ponto final". Um modelo de 9B obedece na
    /// maioria das vezes — e é o resto que importa. Aqui não se testa o modelo, testa-se o que
    /// acontece com o que ele devolve.
    /// </para>
    /// </summary>
    public class ChatTitlerTests
    {
        [Theory]
        [InlineData("Bug de largura no balão", "Bug de largura no balão")]
        [InlineData("\"Bug de largura no balão\"", "Bug de largura no balão")]
        [InlineData("Título: Refatoração da interface", "Refatoração da interface")]
        [InlineData("Refatoração da interface.", "Refatoração da interface")]
        [InlineData("  **Memória hierárquica**  ", "Memória hierárquica")]
        [InlineData("Ajuste no\npainel lateral", "Ajuste no")]
        public void OQueOModeloAcrescenta_SaiFora(string bruto, string esperado)
        {
            ChatTitler.Limpar(bruto).Should().Be(esperado);
        }

        [Fact]
        public void RaciocinioNaResposta_NaoViraTitulo()
        {
            // Think:false é pedido, mas o campo não existe em todo provider e nem todo modelo
            // respeita. Sem isto o cabeçalho mostraria o rascunho do modelo.
            ChatTitler.Limpar("<think>o usuário quer um nome curto</think>Cache de prefixo")
                .Should().Be("Cache de prefixo");
        }

        [Fact]
        public void RespostaVaziaOuLongaDemais_NaoTitula()
        {
            // null devolvido quer dizer "mantenha o que já tinha". Meio parágrafo pendurado no
            // cabeçalho é pior que a heurística antiga.
            ChatTitler.Limpar(null).Should().BeNull();
            ChatTitler.Limpar("   ").Should().BeNull();
            ChatTitler.Limpar(new string('x', ChatTitler.MaxCaracteres + 1)).Should().BeNull();
        }

        [Fact]
        public void NoLimiteExato_AindaTitula()
        {
            ChatTitler.Limpar(new string('x', ChatTitler.MaxCaracteres))
                .Should().HaveLength(ChatTitler.MaxCaracteres);
        }

        [Fact]
        public void OMaterial_LevaAsDuasFalasEAparaAsDuas()
        {
            string material = ChatTitler.Material(
                new string('u', 900),
                "<think>rascunho</think>" + new string('a', 900),
                limitePorLado: 100);

            material.Should().StartWith("Usuário: ");
            material.Should().Contain("Agente: ");
            material.Should().NotContain("<think>", "raciocínio não descreve o assunto");
            material.Length.Should().BeLessThan(300, "as duas pontas são aparadas");
        }
    }
}
