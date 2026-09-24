using System.Collections.Generic;
using AIB.Services.Agent;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O freio da chamada idêntica que "dá certo" sem mudar nada. O bloqueio antigo só pegava a
    /// que falhou; no Bitrix o modelo rolou 40 vezes um quadro que não rolava, com o mesmo
    /// raciocínio palavra por palavra.
    /// </summary>
    public class RepeticaoNoTurnoTests
    {
        [Fact]
        public void AteOAviso_Segue_DepoisAvisa_ENoTetoPara()
        {
            var contagem = new Dictionary<string, int>();
            string a = AgentLoop.Assinatura("browser", "{\"action\":\"scroll\"}");

            for (int i = 1; i < AgentLoop.AvisoDeRepeticao; i++)
                AgentLoop.ContarRepeticao(contagem, a).Should().Be((false, (string?)null), $"a {i}ª é legítima");

            var (bloqueia, aviso) = AgentLoop.ContarRepeticao(contagem, a);
            bloqueia.Should().BeFalse();
            aviso.Should().Contain($"{AgentLoop.AvisoDeRepeticao}ª chamada idêntica");

            for (int i = AgentLoop.AvisoDeRepeticao + 1; i < AgentLoop.TetoDeRepeticao; i++)
                AgentLoop.ContarRepeticao(contagem, a).Bloquear.Should().BeFalse();

            AgentLoop.ContarRepeticao(contagem, a).Bloquear.Should().BeTrue();
        }

        [Fact]
        public void ArgumentoDiferente_EhOutraConta()
        {
            var contagem = new Dictionary<string, int>();
            for (int i = 0; i < AgentLoop.TetoDeRepeticao - 1; i++)
                AgentLoop.ContarRepeticao(contagem, AgentLoop.Assinatura("browser", "{\"action\":\"scroll\"}"));

            AgentLoop.ContarRepeticao(contagem, AgentLoop.Assinatura("browser", "{\"action\":\"scroll\",\"ref\":\"e8\"}"))
                .Should().Be((false, (string?)null));
        }
    }
}
