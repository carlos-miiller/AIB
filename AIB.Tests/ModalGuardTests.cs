using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Supressão do ghosting enquanto há modal do AIB na tela.
    /// <para>
    /// O sintoma real: abrir o modal de confirmação de comando tirava o foco da ChatWindow, o
    /// handler .Deactivated disparava, e o chat sumia atrás do próprio diálogo — o usuário
    /// precisava do atalho global para trazê-lo de volta.
    /// </para>
    /// </summary>
    public class ModalGuardTests
    {
        [Fact]
        public void SemModal_OGhostingContinuaAtivo()
        {
            ModalGuard.IsAnyModalOpen.Should().BeFalse(
                "clicar fora do AIB tem de continuar escondendo o chat — é a UX de stealth documentada");
        }

        [Fact]
        public void DentroDoEscopo_OGhostingFicaSuprimido()
        {
            using (ModalGuard.Enter())
            {
                ModalGuard.IsAnyModalOpen.Should().BeTrue();
            }

            ModalGuard.IsAnyModalOpen.Should().BeFalse("fechado o modal, o ghosting volta");
        }

        [Fact]
        public void ModaisAninhados_SoLiberamQuandoOUltimoFecha()
        {
            // Contador e não booleano: um flag seria zerado pelo primeiro a fechar, e o chat
            // sumiria com o segundo modal ainda na tela.
            var externo = ModalGuard.Enter();
            var interno = ModalGuard.Enter();

            interno.Dispose();
            ModalGuard.IsAnyModalOpen.Should().BeTrue("o modal externo ainda está aberto");

            externo.Dispose();
            ModalGuard.IsAnyModalOpen.Should().BeFalse();
        }

        [Fact]
        public void DisposeDuplo_NaoDesligaOGhostingPermanentemente()
        {
            var escopo = ModalGuard.Enter();
            escopo.Dispose();
            escopo.Dispose();

            using (ModalGuard.Enter())
            {
                ModalGuard.IsAnyModalOpen.Should().BeTrue(
                    "um decremento a mais deixaria o contador negativo e mataria o ghosting pelo resto da sessão");
            }
        }
    }
}
