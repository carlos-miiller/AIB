using System;
using AIB.Services;
using AIB.Services.Memory;
using AIB.Views;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// Como uma habilidade executada aparece na trilha de ações e na aba de registro.
    /// <para>
    /// Duas falhas na mesma tela, com a mesma raiz: a <c>skill</c> não tinha extrator
    /// de artefato. Na trilha o tooltip mostrava só "skill", sem dizer QUAL habilidade
    /// rodou; no registro do painel a linha saía inteiramente em branco, porque o nome da
    /// ferramenta era lido do artefato — que não existia.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class SkillActionDisplayTests : IDisposable
    {
        public SkillActionDisplayTests() => ActionLogService.Clear();

        public void Dispose() => ActionLogService.Clear();

        private const string ArgsPlanilha =
            """{"skill_name":"ler-planilha","arguments":"-Path C:\\lista.xlsx"}""";

        [Fact]
        public void ChamadaDeHabilidade_VirouArtefatoComONomeDela()
        {
            var artefato = ArtifactExtractor.Construir("skill", ArgsPlanilha, "Ramal\tNome");

            artefato.Should().NotBeNull();
            artefato!.Value.Should().Contain("ler-planilha", "o literal tem de dizer QUAL habilidade rodou");
            artefato.Value.Should().Contain("-Path", "os argumentos fazem parte da chamada");
            artefato.Failed.Should().BeFalse();
        }

        [Fact]
        public void HabilidadeRecusada_ViraArtefatoDeRecusa()
        {
            var artefato = ArtifactExtractor.Construir(
                "skill", ArgsPlanilha, "Ação Rejeitada pelo Usuário.");

            artefato.Should().NotBeNull();
            artefato!.Kind.Should().Be(ArtifactKind.Denied);
            artefato.Value.Should().Contain("ler-planilha");
        }

        [Fact]
        public void SemNomeDeHabilidade_NaoHaArtefato()
        {
            // Sem o nome nada foi executado, e registrar a chamada vazia como fato criaria uma
            // linha que não corresponde a nada que aconteceu.
            ArtifactExtractor.Construir("skill", "{}", "ERRO: ...").Should().BeNull();
        }

        [Fact]
        public void ChipEmExecucao_MostraONomeDaHabilidade()
        {
            // O chip nasce ANTES do resultado, então ele não tem artefato: o resumo sai direto
            // dos argumentos da chamada.
            ArtifactExtractor.ResumirArgumento("skill", ArgsPlanilha)
                .Should().Contain("ler-planilha");
        }

        [Fact]
        public void AcaoSemArtefato_NaoVaiEmBrancoParaORegistro()
        {
            // A regressão de verdade: o registro tirava o nome da ferramenta do ARTEFATO. Toda
            // ferramenta sem extrator próprio virava uma linha vazia na aba de logs — não uma
            // linha errada, uma linha em branco, que não dá nem para desconfiar do que foi.
            ChatWindow.RegistrarAcao(new ChatStreamItem.ToolFinished(
                Id: "1", Tool: "ferramenta_sem_extrator", Failed: false, Denied: false,
                Artifact: null, Detail: null));

            ActionLogService.Entries.Should().ContainSingle();
            ActionLogService.Entries[0].Tool.Should().Be("ferramenta_sem_extrator");
            ActionLogService.Entries[0].Target.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public void HabilidadeNoRegistro_MostraONomeDela()
        {
            var artefato = ArtifactExtractor.Construir("skill", ArgsPlanilha, "Ramal\tNome");

            ChatWindow.RegistrarAcao(new ChatStreamItem.ToolFinished(
                Id: "1", Tool: "skill", Failed: false, Denied: false,
                Artifact: artefato, Detail: null));

            ActionLogService.Entries[0].FullTarget.Should().Contain("ler-planilha");
        }
    }
}
